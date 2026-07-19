using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace VComTunnel.Core;

public enum SerialTrafficDirection
{
    Rx,
    Tx
}

/// <summary>
/// Copies serial payloads to bounded background writers. RX and TX always use
/// separate files so binary capture remains lossless and direction-safe.
/// Recording must never hold the tunnel data path or alter RFC2217 ordering.
/// </summary>
public sealed class SerialTrafficRecorder : IDisposable
{
    private const int QueueCapacity = 2048;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly TunnelMapping _mapping;
    private readonly SerialTrafficLogOptions _options;
    private readonly InMemoryLog _log;
    private readonly string _logsDirectory;
    private readonly string _activePath;
    private readonly Channel<TrafficRecord> _queue = Channel.CreateBounded<TrafficRecord>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<SerialTrafficDirection, AnsiColorCodeFilter> _textFilters = new();
    private readonly Dictionary<SerialTrafficDirection, bool> _textLineStarts = new();
    private readonly Task _writer;
    private long _lastDropWarningTicks;
    private int _disposed;

    public SerialTrafficRecorder(
        TunnelMapping mapping,
        SerialTrafficLogOptions options,
        InMemoryLog log,
        string? logsDirectory = null)
    {
        _mapping = mapping;
        _options = options;
        _log = log;
        _logsDirectory = ResolveLogsDirectory(options, logsDirectory);
        _activePath = PathFor(PrimaryDirection(options));
        _writer = Task.Run(WriteLoopAsync);
        _log.Info(mapping.Name, $"Serial traffic recording enabled: {_logsDirectory}.");
    }

    public string ActivePath => _activePath;

    public static string GetActivePath(TunnelMapping mapping) =>
        Path.Combine(
            ResolveLogsDirectory(mapping.TrafficLog, null),
            BuildFileName(
                mapping,
                mapping.TrafficLog.Format,
                PrimaryDirection(mapping.TrafficLog)));

    private static string ResolveLogsDirectory(
        SerialTrafficLogOptions options,
        string? overrideDirectory) =>
        !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : !string.IsNullOrWhiteSpace(options.DirectoryPath)
                ? options.DirectoryPath
                : AppPaths.SerialTrafficLogsDirectory;

    public void Record(SerialTrafficDirection direction, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0
            || (direction == SerialTrafficDirection.Rx && !_options.CaptureRx)
            || (direction == SerialTrafficDirection.Tx && !_options.CaptureTx)
            || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var record = new TrafficRecord(DateTimeOffset.UtcNow, direction, bytes.ToArray());
        if (_queue.Writer.TryWrite(record))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _lastDropWarningTicks);
        if (next == 0 || now >= next)
        {
            Interlocked.Exchange(ref _lastDropWarningTicks, now + Stopwatch.Frequency);
            _log.Warn(_mapping.Name, "Serial traffic log queue is full; dropping log records without blocking the tunnel.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        if (!_writer.Wait(TimeSpan.FromSeconds(2)))
        {
            _stop.Cancel();
            try
            {
                _writer.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
        }
        _stop.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        var streams = new Dictionary<SerialTrafficDirection, FileStream>();
        Task flushDelay = Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
        try
        {
            while (true)
            {
                var dataReady = _queue.Reader.WaitToReadAsync(_stop.Token).AsTask();
                var completed = await Task.WhenAny(dataReady, flushDelay).ConfigureAwait(false);
                if (completed == flushDelay)
                {
                    await flushDelay.ConfigureAwait(false);
                    foreach (var stream in streams.Values)
                    {
                        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                    }
                    flushDelay = Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
                    continue;
                }

                if (!await dataReady.ConfigureAwait(false))
                {
                    break;
                }

                while (_queue.Reader.TryRead(out var record))
                {
                    var encoded = FormatRecord(record);
                    if (encoded.Length > 0)
                    {
                        await WriteAsync(record.Direction, encoded, streams).ConfigureAwait(false);
                    }
                }
            }

            if (IsTextFormat(_options.Format))
            {
                foreach (var (direction, filter) in _textFilters)
                {
                    var pending = filter.Flush();
                    if (pending.Length > 0)
                    {
                        var encoded = _options.IncludeTimestamp
                            ? AddLineTimestamps(direction, pending, DateTimeOffset.UtcNow)
                            : pending;
                        await WriteAsync(direction, encoded, streams).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn(_mapping.Name, $"Serial traffic recording stopped: {ex.Message}");
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                try
                {
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                stream.Dispose();
            }
        }
    }

    private async Task WriteAsync(
        SerialTrafficDirection direction,
        byte[] encoded,
        Dictionary<SerialTrafficDirection, FileStream> streams)
    {
        if (!streams.TryGetValue(direction, out var stream)
            || stream.Length + encoded.Length > (long)_options.MaxFileSizeMb * 1024 * 1024)
        {
            stream?.Dispose();
            stream = OpenActiveFile(direction, rotate: File.Exists(PathFor(direction)));
            streams[direction] = stream;
        }

        await stream.WriteAsync(encoded, _stop.Token).ConfigureAwait(false);
    }

    private FileStream OpenActiveFile(SerialTrafficDirection direction, bool rotate)
    {
        var path = PathFor(direction);
        Directory.CreateDirectory(_logsDirectory);
        if (rotate)
        {
            RotateFiles(path);
        }

        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private void RotateFiles(string activePath)
    {
        var oldest = $"{activePath}.{_options.MaxFiles - 1}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _options.MaxFiles - 2; index >= 1; index--)
        {
            var source = $"{activePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{activePath}.{index + 1}", overwrite: true);
            }
        }

        if (_options.MaxFiles > 1 && File.Exists(activePath))
        {
            File.Move(activePath, $"{activePath}.1", overwrite: true);
        }
        else if (File.Exists(activePath))
        {
            File.Delete(activePath);
        }
    }

    private byte[] FormatRecord(TrafficRecord record)
    {
        if (_options.Format == SerialTrafficLogFormat.RawBinary)
        {
            return record.Bytes;
        }

        if (IsTextFormat(_options.Format))
        {
            if (!_textFilters.TryGetValue(record.Direction, out var filter))
            {
                filter = new AnsiColorCodeFilter();
                _textFilters[record.Direction] = filter;
            }
            var filtered = filter.Add(record.Bytes);
            return _options.IncludeTimestamp
                ? AddLineTimestamps(record.Direction, filtered, record.Timestamp)
                : filtered;
        }

        var prefix = _options.IncludeTimestamp ? $"{record.Timestamp:O} " : "";
        var direction = record.Direction == SerialTrafficDirection.Rx ? "RX" : "TX";
        var payload = _options.Format == SerialTrafficLogFormat.Hex
            ? Convert.ToHexString(record.Bytes)
            : FormatLegacyEscapedText(record.Bytes);
        return Utf8NoBom.GetBytes($"{prefix}{direction} {record.Bytes.Length} {payload}\n");
    }

    private byte[] AddLineTimestamps(
        SerialTrafficDirection direction,
        byte[] bytes,
        DateTimeOffset timestamp)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        var lineStart = !_textLineStarts.TryGetValue(direction, out var stored) || stored;
        using var output = new MemoryStream(bytes.Length + 64);
        foreach (var value in bytes)
        {
            if (lineStart)
            {
                output.Write(Utf8NoBom.GetBytes($"{timestamp:O} "));
                lineStart = false;
            }
            output.WriteByte(value);
            if (value == (byte)'\n')
            {
                lineStart = true;
            }
        }
        _textLineStarts[direction] = lineStart;
        return output.ToArray();
    }

    private string PathFor(SerialTrafficDirection direction) =>
        Path.Combine(_logsDirectory, BuildFileName(_mapping, _options.Format, direction));

    private static SerialTrafficDirection PrimaryDirection(SerialTrafficLogOptions options) =>
        options.CaptureRx ? SerialTrafficDirection.Rx : SerialTrafficDirection.Tx;

    private static bool IsTextFormat(SerialTrafficLogFormat format) =>
        format == SerialTrafficLogFormat.Text;

    private static string FormatLegacyEscapedText(byte[] bytes)
    {
        var text = Utf8NoBom.GetString(bytes);
        var result = new StringBuilder(text.Length);
        foreach (var value in text)
        {
            result.Append(value switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\\' => "\\\\",
                >= ' ' => value.ToString(),
                _ => $"\\u{(int)value:X4}"
            });
        }
        return result.ToString();
    }

    private static string BuildFileName(
        TunnelMapping mapping,
        SerialTrafficLogFormat format,
        SerialTrafficDirection direction)
    {
        var port = SanitizeFileNamePart(mapping.VisiblePort, "COM");
        var id = SanitizeFileNamePart(mapping.Id, "mapping");
        var directionSuffix = direction == SerialTrafficDirection.Rx ? "rx" : "tx";
        var extension = format switch
        {
            SerialTrafficLogFormat.RawBinary => "bin",
            SerialTrafficLogFormat.Text => "txt",
            _ => "log"
        };
        return $"{port}-{id}-traffic.{directionSuffix}.{extension}";
    }

    private static string SanitizeFileNamePart(string? value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((value ?? "").Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private sealed record TrafficRecord(
        DateTimeOffset Timestamp,
        SerialTrafficDirection Direction,
        byte[] Bytes);

    private sealed class AnsiColorCodeFilter
    {
        private readonly List<byte> _candidate = [];
        private int _state;

        public byte[] Add(ReadOnlySpan<byte> bytes)
        {
            var output = new List<byte>(bytes.Length);
            foreach (var value in bytes)
            {
                switch (_state)
                {
                    case 0 when value == 0x1B:
                        _candidate.Add(value);
                        _state = 1;
                        break;
                    case 0:
                        output.Add(value);
                        break;
                    case 1 when value == (byte)'[':
                        _candidate.Add(value);
                        _state = 2;
                        break;
                    case 1:
                        output.AddRange(_candidate);
                        _candidate.Clear();
                        _state = 0;
                        if (value == 0x1B)
                        {
                            _candidate.Add(value);
                            _state = 1;
                        }
                        else
                        {
                            output.Add(value);
                        }
                        break;
                    case 2:
                        _candidate.Add(value);
                        if (value is >= 0x40 and <= 0x7E)
                        {
                            if (value != (byte)'m')
                            {
                                output.AddRange(_candidate);
                            }
                            _candidate.Clear();
                            _state = 0;
                        }
                        else if (_candidate.Count > 64)
                        {
                            output.AddRange(_candidate);
                            _candidate.Clear();
                            _state = 0;
                        }
                        break;
                }
            }
            return output.ToArray();
        }

        public byte[] Flush()
        {
            var pending = _candidate.ToArray();
            _candidate.Clear();
            _state = 0;
            return pending;
        }
    }
}
