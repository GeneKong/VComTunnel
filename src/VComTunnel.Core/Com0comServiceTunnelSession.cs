using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VComTunnel.Core;

public sealed class Com0comServiceTunnelSession : IManagedTunnelSession
{
    private const int MaxFrameBytes = 4096;
    private static readonly TimeSpan CommandAckTimeout = Rfc2217Client.RecommendedCommandAckTimeout;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAlivePollInterval = TimeSpan.FromSeconds(5);

    private readonly TunnelMapping _mapping;
    private readonly InMemoryLog _log;
    private readonly Action<IManagedTunnelSession, string> _faulted;
    private readonly ISerialPortEndpointFactory _serialPorts;
    private readonly Rfc2217Client _rfc2217 = new();
    private readonly object _ackLock = new();
    private readonly List<Rfc2217ExpectedAck> _pendingAcks = [];
    private readonly object _flowLock = new();
    private readonly SemaphoreSlim _networkWriteLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _pendingAckCompletion;
    private TaskCompletionSource? _flowResumeCompletion;
    private bool _remoteFlowSuspended;
    private long _lastNetworkActivityTicks;
    private ISerialPortEndpoint? _serial;
    private TcpClient? _tcp;
    private Task? _serialLoop;
    private Task? _networkLoop;
    private Task? _keepAliveLoop;
    private int _disposed;

    public Com0comServiceTunnelSession(
        TunnelMapping mapping,
        InMemoryLog log,
        Action<IManagedTunnelSession, string> faulted,
        ISerialPortEndpointFactory? serialPorts = null)
    {
        _mapping = mapping;
        _log = log;
        _faulted = faulted;
        _serialPorts = serialPorts ?? new Win32SerialPortEndpointFactory();
        State = TunnelRunState.Starting;
        MarkNetworkActivity();
    }

    public TunnelRunState State { get; private set; }
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_mapping.BackingPort))
        {
            throw new InvalidOperationException("backingPort is required for com0comService mappings.");
        }

        try
        {
            _serial = _serialPorts.Open(_mapping.BackingPort);
        }
        catch (SerialPortOpenException ex)
        {
            throw new IOException(BuildBackingPortOpenError(_mapping, ex), ex);
        }

        _tcp = new TcpClient();
        try
        {
            await _tcp.ConnectAsync(_mapping.Host, _mapping.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new IOException($"Could not connect to RFC2217 endpoint {_mapping.Host}:{_mapping.Port}: {ex.Message}", ex);
        }

        var stream = _tcp.GetStream();
        _networkLoop = Task.Run(NetworkLoopAsync);
        await SendFrameWithAckAsync(
            stream,
            new Rfc2217OutboundFrame(
                _rfc2217.BuildInitialNegotiation(),
                Rfc2217Client.BuildInitialExpectedAcks(),
                "initial-negotiation",
                ContinueOnAckTimeout: true)).ConfigureAwait(false);
        await SendFrameWithAckAsync(
            stream,
            new Rfc2217OutboundFrame(
                Rfc2217Client.BuildStartupStatusQuery(),
                [],
                "startup-status-query")).ConfigureAwait(false);

        State = TunnelRunState.Running;
        _log.Info(_mapping.Name, $"Started com0com service tunnel {_mapping.BackingPort} -> {_mapping.Host}:{_mapping.Port}.");

        _serialLoop = Task.Run(SerialLoopAsync);
        _keepAliveLoop = Task.Run(KeepAliveLoopAsync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        State = LastError is null ? TunnelRunState.Stopped : TunnelRunState.Faulted;
        _stop.Cancel();
        _tcp?.Dispose();
        _serial?.Dispose();
        _stop.Dispose();
    }

    private async Task SerialLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            var buffer = new byte[MaxFrameBytes];
            while (!_stop.IsCancellationRequested)
            {
                var read = await _serial!.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    continue;
                }

                await WaitForRemoteFlowAsync().ConfigureAwait(false);
                await WriteNetworkAsync(stream, Rfc2217Client.EscapeSerialData(buffer, 0, read)).ConfigureAwait(false);
                _log.Info(_mapping.Name, $"Serial TX {read} byte(s) from {_mapping.BackingPort}.");
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private async Task NetworkLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            var buffer = new byte[MaxFrameBytes];
            while (!_stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Remote endpoint closed the TCP connection.");
                }
                MarkNetworkActivity();

                var frame = _rfc2217.ProcessNetworkBytes(buffer, read);
                if (frame.Replies.Length > 0)
                {
                    await WriteNetworkAsync(stream, frame.Replies).ConfigureAwait(false);
                }

                foreach (var option in frame.TelnetOptions)
                {
                    ApplyTelnetOption(option);
                }

                foreach (var notification in frame.Notifications)
                {
                    ApplyNotification(notification);
                }

                if (frame.SerialData.Length > 0)
                {
                    await _serial!.WriteAsync(frame.SerialData, _stop.Token).ConfigureAwait(false);
                    _log.Info(_mapping.Name, $"RFC2217 RX serial {frame.SerialData.Length} byte(s) to {_mapping.BackingPort}.");
                }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private async Task KeepAliveLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(KeepAlivePollInterval, _stop.Token).ConfigureAwait(false);
                var idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastNetworkActivityTicks));
                if (idle >= KeepAliveInterval)
                {
                    await WriteNetworkAsync(stream, Rfc2217Client.BuildTelnetNop()).ConfigureAwait(false);
                    _log.Info(_mapping.Name, "RFC2217 keep-alive NOP sent.");
                }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private void ApplyNotification(Rfc2217Notification notification)
    {
        if (Rfc2217Client.IsCommandAck(notification.Command))
        {
            var pendingAck = CompletePendingAck(notification);
            if (pendingAck == PendingAckResult.CompletedWithAcceptedSetControl)
            {
                _log.Warn(_mapping.Name, $"RFC2217 peer accepted SET-CONTROL {DescribeSetControlNotification(notification)}.");
                return;
            }

            if (pendingAck != PendingAckResult.NotPending)
            {
                return;
            }

            if (notification.Command == Rfc2217Client.AckSetControl && notification.Payload.Length == 1)
            {
                _log.Info(_mapping.Name, $"RFC2217 remote control state {DescribeSetControlNotification(notification)}.");
                return;
            }

            if (Rfc2217Client.IsAcceptedSerialSetting(notification))
            {
                _log.Info(_mapping.Name, $"RFC2217 remote serial setting {Rfc2217ExpectedAck.Describe(notification)}.");
            }

            return;
        }

        if (Rfc2217Client.IsFlowControlCommand(notification.Command))
        {
            SetRemoteFlowSuspended(notification.Command == Rfc2217Client.FlowControlSuspend);
            return;
        }

        if (notification.Payload.Length == 0)
        {
            return;
        }

        if (notification.Command == Rfc2217Client.NotifyModemState)
        {
            _log.Info(_mapping.Name, $"RFC2217 modem state 0x{notification.Payload[0]:X2}.");
        }
        else if (notification.Command == Rfc2217Client.NotifyLineState)
        {
            _log.Info(_mapping.Name, $"RFC2217 line state 0x{notification.Payload[0]:X2}.");
        }
    }

    private void ApplyTelnetOption(Rfc2217TelnetOptionEvent option)
    {
        if (option.Option == Rfc2217Client.TelnetOptionComPortControl && option.Rejected)
        {
            throw new IOException($"RFC2217 endpoint rejected Telnet COM-PORT-OPTION ({option.Describe()}).");
        }

        if (option.Accepted)
        {
            _log.Info(_mapping.Name, $"RFC2217 Telnet negotiation {option.Describe()}.");
        }
        else
        {
            _log.Warn(_mapping.Name, $"RFC2217 Telnet negotiation {option.Describe()}.");
        }
    }

    private async Task SendFrameWithAckAsync(NetworkStream stream, Rfc2217OutboundFrame frame)
    {
        if (frame.ExpectedAckCommands.Length == 0)
        {
            await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var wait = PrepareAckWait(frame.ExpectedAckCommands);
            await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);

            try
            {
                await wait.WaitAsync(CommandAckTimeout, _stop.Token).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                ClearPendingAckWait();
                _log.Warn(_mapping.Name, $"RFC2217 {frame.Description} ack timed out{(attempt == 0 ? ", retrying" : "")}.");
            }
        }

        if (frame.ContinueOnAckTimeout)
        {
            _log.Warn(_mapping.Name, $"RFC2217 {frame.Description} ack timed out after retry; continuing with degraded remote-status notifications.");
            return;
        }

        throw new IOException($"RFC2217 {frame.Description} ack timed out after retry.");
    }

    private async Task WriteNetworkAsync(NetworkStream stream, byte[] bytes)
    {
        await _networkWriteLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            MarkNetworkActivity();
        }
        finally
        {
            _networkWriteLock.Release();
        }
    }

    private Task PrepareAckWait(IReadOnlyList<Rfc2217ExpectedAck> expectedAcks)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_ackLock)
        {
            _pendingAcks.Clear();
            _pendingAcks.AddRange(expectedAcks);
            _pendingAckCompletion = completion;
        }

        return completion.Task;
    }

    private void ClearPendingAckWait()
    {
        lock (_ackLock)
        {
            _pendingAcks.Clear();
            _pendingAckCompletion = null;
        }
    }

    private PendingAckResult CompletePendingAck(Rfc2217Notification notification)
    {
        TaskCompletionSource? completion = null;
        Exception? failure = null;
        var result = PendingAckResult.NotPending;
        lock (_ackLock)
        {
            var index = _pendingAcks.FindIndex(expected => expected.Matches(notification));
            if (index >= 0)
            {
                result = PendingAckResult.Completed;
                _pendingAcks.RemoveAt(index);
                if (_pendingAcks.Count == 0)
                {
                    completion = _pendingAckCompletion;
                    _pendingAckCompletion = null;
                }
            }
            else if ((index = _pendingAcks.FindIndex(expected => expected.MatchesAcceptedSetControlValue(notification))) >= 0)
            {
                result = PendingAckResult.CompletedWithAcceptedSetControl;
                _pendingAcks.RemoveAt(index);
                if (_pendingAcks.Count == 0)
                {
                    completion = _pendingAckCompletion;
                    _pendingAckCompletion = null;
                }
            }
            else if (_pendingAcks.Any(expected => expected.IsSameCommand(notification)))
            {
                result = PendingAckResult.Failed;
                var expected = string.Join(", ", _pendingAcks.Select(ack => ack.Describe()));
                failure = new IOException($"RFC2217 ack returned unexpected value {Rfc2217ExpectedAck.Describe(notification)}; expected {expected}.");
                completion = _pendingAckCompletion;
                _pendingAcks.Clear();
                _pendingAckCompletion = null;
            }
        }

        if (result == PendingAckResult.NotPending)
        {
            return result;
        }

        if (failure is not null)
        {
            completion?.TrySetException(failure);
        }
        else
        {
            completion?.TrySetResult();
        }

        return result;
    }

    private Task WaitForRemoteFlowAsync()
    {
        lock (_flowLock)
        {
            return _remoteFlowSuspended
                ? (_flowResumeCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(_stop.Token)
                : Task.CompletedTask;
        }
    }

    private void SetRemoteFlowSuspended(bool suspended)
    {
        TaskCompletionSource? resume = null;
        lock (_flowLock)
        {
            if (_remoteFlowSuspended == suspended)
            {
                return;
            }

            _remoteFlowSuspended = suspended;
            if (!suspended)
            {
                resume = _flowResumeCompletion;
                _flowResumeCompletion = null;
            }
        }

        if (suspended)
        {
            _log.Warn(_mapping.Name, "RFC2217 peer requested flow-control suspend.");
        }
        else
        {
            _log.Info(_mapping.Name, "RFC2217 peer resumed flow-control.");
            resume?.TrySetResult();
        }
    }

    private void Fault(Exception ex)
    {
        LastError = ex.Message;
        State = TunnelRunState.Faulted;
        _log.Error(_mapping.Name, $"com0com service tunnel faulted: {ex.Message}");
        _faulted(this, ex.Message);
        Dispose();
    }

    private void MarkNetworkActivity()
    {
        Interlocked.Exchange(ref _lastNetworkActivityTicks, Stopwatch.GetTimestamp());
    }

    private static string DescribeSetControlNotification(Rfc2217Notification notification)
    {
        return notification.Payload.Length == 1
            ? $"{notification.Payload[0]} ({Rfc2217Client.DescribeSetControlValue(notification.Payload[0])})"
            : $"{notification.Payload.Length} byte(s)";
    }

    public static string BuildBackingPortOpenError(TunnelMapping mapping, SerialPortOpenException exception)
    {
        var pairHint = !string.IsNullOrWhiteSpace(mapping.BackingPort)
            ? $" Mapping expects {mapping.VisiblePort} <-> {mapping.BackingPort}."
            : "";
        var createHint = !string.IsNullOrWhiteSpace(mapping.BackingPort)
            ? $" If the pair is missing, create it first: setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}."
            : "";
        var busyHint = exception.NativeErrorCode == 5
            ? " ERROR 5 usually means the backing port is already open by another mapping, hub4com/com2tcp, or a serial tool; stop that process and retry."
            : "";

        return $"{exception.Message}{pairHint}{createHint}{busyHint}";
    }

    private enum PendingAckResult
    {
        NotPending,
        Completed,
        CompletedWithAcceptedSetControl,
        Failed
    }

    private sealed record Rfc2217OutboundFrame(
        byte[] Bytes,
        Rfc2217ExpectedAck[] ExpectedAckCommands,
        string Description,
        bool ContinueOnAckTimeout = false);
}

public interface ISerialPortEndpoint : IDisposable
{
    Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken);
    Task WriteAsync(byte[] bytes, CancellationToken cancellationToken);
}

public interface ISerialPortEndpointFactory
{
    ISerialPortEndpoint Open(string portName);
}

public sealed class SerialPortOpenException : IOException
{
    public SerialPortOpenException(string portName, string path, int nativeErrorCode, string operation)
        : base(BuildMessage(path, nativeErrorCode, operation))
    {
        PortName = portName;
        Path = path;
        NativeErrorCode = nativeErrorCode;
    }

    public string PortName { get; }
    public string Path { get; }
    public int NativeErrorCode { get; }

    private static string BuildMessage(string path, int nativeErrorCode, string operation)
    {
        var nativeMessage = new Win32Exception(nativeErrorCode).Message;
        return $"Could not {operation} serial port {path}: ERROR {nativeErrorCode} - {nativeMessage}.";
    }
}

public sealed class Win32SerialPortEndpointFactory : ISerialPortEndpointFactory
{
    public ISerialPortEndpoint Open(string portName) => Win32SerialPortEndpoint.Open(portName);
}

internal sealed class Win32SerialPortEndpoint : ISerialPortEndpoint
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private readonly SafeFileHandle _handle;

    private Win32SerialPortEndpoint(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static Win32SerialPortEndpoint Open(string portName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("com0comService backend is only available on Windows.");
        }

        var path = portName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? portName
            : $@"\\.\{portName}";
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SerialPortOpenException(portName, path, error, "open");
        }

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = 200,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 2000
        };
        if (!SetCommTimeouts(handle, ref timeouts))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SerialPortOpenException(portName, path, error, "configure serial port timeouts for");
        }

        return new Win32SerialPortEndpoint(handle);
    }

    public Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadFile(_handle, buffer, buffer.Length, out var bytesRead, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return bytesRead;
        }, cancellationToken);
    }

    public Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = bytes.Length - offset;
                var chunk = new byte[remaining];
                Buffer.BlockCopy(bytes, offset, chunk, 0, remaining);
                if (!WriteFile(_handle, chunk, chunk.Length, out var written, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (written <= 0)
                {
                    throw new IOException("Serial port write completed with zero bytes.");
                }

                offset += written;
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle hFile, ref CommTimeouts lpCommTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);
}
