using System.Collections.Concurrent;

namespace VComTunnel.Core;

/// <summary>
/// Owns persistent serial logging jobs inside the Windows service. The UI only
/// changes configuration; enabled exclusive jobs keep running after the UI exits
/// and retry when another process temporarily owns the COM port.
/// </summary>
public sealed class SerialBackgroundLogManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExclusiveSerialLogSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ISerialPortEndpointFactory _serialPorts;
    private readonly InMemoryLog _log;
    private readonly string? _logsDirectory;
    private readonly TimeSpan _retryDelay;
    private int _disposed;

    public SerialBackgroundLogManager(InMemoryLog log)
        : this(new Win32SerialPortEndpointFactory(), log)
    {
    }

    public SerialBackgroundLogManager(
        ISerialPortEndpointFactory serialPorts,
        InMemoryLog log,
        string? logsDirectory = null,
        TimeSpan? retryDelay = null)
    {
        _serialPorts = serialPorts;
        _log = log;
        _logsDirectory = logsDirectory;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task SyncAsync(IEnumerable<TunnelMapping> mappings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var desired = mappings
            .Where(IsExclusiveLogEnabled)
            .ToDictionary(mapping => mapping.Id, StringComparer.OrdinalIgnoreCase);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var existing in _sessions.ToArray())
            {
                if (desired.TryGetValue(existing.Key, out var mapping) && existing.Value.Matches(mapping))
                {
                    continue;
                }

                if (_sessions.TryRemove(existing.Key, out var removed))
                {
                    await removed.DisposeAsync();
                }
            }

            foreach (var mapping in desired.Values)
            {
                if (_sessions.ContainsKey(mapping.Id))
                {
                    continue;
                }

                var session = new ExclusiveSerialLogSession(
                    mapping,
                    _serialPorts,
                    _log,
                    _logsDirectory,
                    _retryDelay);
                if (_sessions.TryAdd(mapping.Id, session))
                {
                    session.Start();
                }
                else
                {
                    await session.DisposeAsync();
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public SerialTrafficLogStatus GetStatus(TunnelMapping mapping, TunnelStatus? tunnelStatus = null)
    {
        var options = mapping.TrafficLog ?? new SerialTrafficLogOptions();
        if (!options.Enabled)
        {
            return new SerialTrafficLogStatus(
                mapping.Id,
                false,
                SerialTrafficRecorder.GetActivePath(mapping),
                options.Mode,
                false,
                null);
        }

        if (options.Mode == SerialTrafficLogMode.InUse)
        {
            return new SerialTrafficLogStatus(
                mapping.Id,
                true,
                SerialTrafficRecorder.GetActivePath(mapping),
                options.Mode,
                tunnelStatus?.State == TunnelRunState.Running,
                tunnelStatus?.LastError);
        }

        return _sessions.TryGetValue(mapping.Id, out var session)
            ? session.GetStatus()
            : new SerialTrafficLogStatus(
                mapping.Id,
                true,
                SerialTrafficRecorder.GetActivePath(mapping),
                options.Mode,
                false,
                "Background serial log job is waiting to start.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _syncLock.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values)
            {
                await session.DisposeAsync();
            }
            _sessions.Clear();
        }
        finally
        {
            _syncLock.Release();
            _syncLock.Dispose();
        }
    }

    private static bool IsExclusiveLogEnabled(TunnelMapping mapping) =>
        mapping.TrafficLog is { Enabled: true, Mode: SerialTrafficLogMode.Exclusive };

    private sealed class ExclusiveSerialLogSession : IAsyncDisposable
    {
        private readonly TunnelMapping _mapping;
        private readonly ISerialPortEndpointFactory _serialPorts;
        private readonly InMemoryLog _log;
        private readonly string? _logsDirectory;
        private readonly TimeSpan _retryDelay;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _stateLock = new();
        private Task? _worker;
        private bool _running;
        private string? _lastError;
        private string _activePath;

        public ExclusiveSerialLogSession(
            TunnelMapping mapping,
            ISerialPortEndpointFactory serialPorts,
            InMemoryLog log,
            string? logsDirectory,
            TimeSpan retryDelay)
        {
            _mapping = mapping;
            _serialPorts = serialPorts;
            _log = log;
            _logsDirectory = logsDirectory;
            _retryDelay = retryDelay;
            _activePath = SerialTrafficRecorder.GetActivePath(mapping);
        }

        public bool Matches(TunnelMapping mapping) =>
            string.Equals(_mapping.VisiblePort, mapping.VisiblePort, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_mapping.Name, mapping.Name, StringComparison.Ordinal)
            && Equals(_mapping.TrafficLog, mapping.TrafficLog);

        public void Start() => _worker ??= Task.Run(RunAsync);

        public SerialTrafficLogStatus GetStatus()
        {
            lock (_stateLock)
            {
                return new SerialTrafficLogStatus(
                    _mapping.Id,
                    true,
                    _activePath,
                    SerialTrafficLogMode.Exclusive,
                    _running,
                    _lastError);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_worker is not null)
            {
                try
                {
                    await _worker;
                }
                catch (OperationCanceledException)
                {
                }
            }
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            string? lastReportedError = null;
            while (!_stop.IsCancellationRequested)
            {
                ISerialPortEndpoint? serial = null;
                SerialTrafficRecorder? recorder = null;
                try
                {
                    serial = _serialPorts.Open(_mapping.VisiblePort, exclusive: true);
                    serial.SetSettings(new SerialPortSettings((uint)_mapping.TrafficLog.BaudRate, 8, 0, 0));
                    serial.SetControlLines(
                        ToControlLineValue(_mapping.TrafficLog.Dtr),
                        ToControlLineValue(_mapping.TrafficLog.Rts));
                    recorder = new SerialTrafficRecorder(_mapping, _mapping.TrafficLog, _log, _logsDirectory);

                    lock (_stateLock)
                    {
                        _running = true;
                        _lastError = null;
                        _activePath = recorder.ActivePath;
                    }
                    lastReportedError = null;
                    _log.Info(_mapping.Name, $"Exclusive background serial logging started on {_mapping.VisiblePort}.");

                    var buffer = new byte[4096];
                    while (!_stop.IsCancellationRequested)
                    {
                        var read = await serial.ReadAsync(buffer, _stop.Token);
                        if (read > 0)
                        {
                            recorder.Record(SerialTrafficDirection.Rx, buffer.AsMemory(0, read));
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        _running = false;
                        _lastError = ex.Message;
                    }
                    if (!string.Equals(lastReportedError, ex.Message, StringComparison.Ordinal))
                    {
                        _log.Warn(_mapping.Name, $"Exclusive background serial logging is waiting for {_mapping.VisiblePort}: {ex.Message}");
                        lastReportedError = ex.Message;
                    }
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _running = false;
                    }
                    recorder?.Dispose();
                    serial?.Dispose();
                }

                try
                {
                    await Task.Delay(_retryDelay, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _log.Info(_mapping.Name, $"Exclusive background serial logging stopped on {_mapping.VisiblePort}.");
        }

        private static bool? ToControlLineValue(SerialControlLinePolicy policy) => policy switch
        {
            SerialControlLinePolicy.Low => false,
            SerialControlLinePolicy.High => true,
            _ => null
        };
    }
}
