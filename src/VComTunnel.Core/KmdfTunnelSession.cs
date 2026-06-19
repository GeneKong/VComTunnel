using System.Diagnostics;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VComTunnel.Core;

public interface IKmdfTunnelSession : IManagedTunnelSession
{
}

public sealed class KmdfTunnelSession : IKmdfTunnelSession
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int EventHeaderSize = 24;
    private const int ErrorBufferOverflow = 111;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const ushort EventTypeTxData = 1;
    private const ushort EventTypeSetBaudRate = 2;
    private const ushort EventTypeSetLineControl = 3;
    private const ushort EventTypeSetModemControl = 4;
    private const ushort EventTypeSetHandflow = 5;
    private const ushort EventTypeSetBreak = 6;
    private const ushort EventTypePurge = 7;
    private const ushort EventTypeLocalFlowControl = 8;
    private const uint ModemControlDtr = 0x00000001;
    private const uint ModemControlRts = 0x00000002;
    private const uint RemoteBaudRate = 0x00000001;
    private const uint RemoteWordLength = 0x00000002;
    private const uint RemoteParity = 0x00000004;
    private const uint RemoteStopBits = 0x00000008;
    private const ushort ProtocolMajor = 1;
    private const ushort ProtocolMinor = 2;
    private const int MaxEventBytes = 4096;
    private const int MaxRxBytes = 4096;
    private static readonly TimeSpan CommandAckTimeout = Rfc2217Client.RecommendedCommandAckTimeout;
    private static readonly TimeSpan RxBackpressureRetryInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAlivePollInterval = TimeSpan.FromSeconds(5);

    private static readonly uint IoctlAttach = CtlCode(0x801);
    private static readonly uint IoctlWaitEvent = CtlCode(0x802);
    private static readonly uint IoctlPushRx = CtlCode(0x804);
    private static readonly uint IoctlSetConnectionState = CtlCode(0x805);
    private static readonly uint IoctlDetach = CtlCode(0x806);
    private static readonly uint IoctlSetModemState = CtlCode(0x807);
    private static readonly uint IoctlSetLineState = CtlCode(0x808);
    private static readonly uint IoctlSetRemoteSettings = CtlCode(0x809);

    private readonly TunnelMapping _mapping;
    private readonly InMemoryLog _log;
    private readonly Action<IKmdfTunnelSession, string> _faulted;
    private readonly bool _suppressInitialControlLineSync;
    private readonly Rfc2217Client _rfc2217 = new();
    private readonly Rfc2217LocalFlowControlState _localFlowControl = new();
    private readonly object _ackLock = new();
    private readonly List<Rfc2217ExpectedAck> _pendingAcks = [];
    private readonly object _flowLock = new();
    private readonly SemaphoreSlim _networkWriteLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _pendingAckCompletion;
    private TaskCompletionSource? _flowResumeCompletion;
    private bool _remoteFlowSuspended;
    private long _lastNetworkActivityTicks;
    private SafeFileHandle? _eventDriver;
    private SafeFileHandle? _commandDriver;
    private TcpClient? _tcp;
    private Task? _eventLoop;
    private Task? _networkLoop;
    private Task? _keepAliveLoop;
    private bool _initialControlLineSyncHandled;
    private int _disposed;

    public KmdfTunnelSession(TunnelMapping mapping, InMemoryLog log, Action<IKmdfTunnelSession, string> faulted)
    {
        _mapping = mapping;
        _log = log;
        _faulted = faulted;
        _suppressInitialControlLineSync = mapping.SuppressInitialControlLineSync;
        State = TunnelRunState.Starting;
        MarkNetworkActivity();
    }

    public TunnelRunState State { get; private set; }
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("KMDF backend is only available on Windows.");
        }

        _eventDriver = OpenDriver(_mapping.VisiblePort);
        _commandDriver = OpenDriver(_mapping.VisiblePort);
        Attach();

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

        SetConnectionState(2);
        State = TunnelRunState.Running;
        _log.Info(_mapping.Name, $"Started KMDF tunnel {_mapping.VisiblePort} -> {_mapping.Host}:{_mapping.Port}.");

        _eventLoop = Task.Run(EventLoopAsync);
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
        _eventDriver?.Dispose();
        _commandDriver?.Dispose();
        _stop.Dispose();
    }

    public static string BuildControlDevicePath(string visiblePort)
    {
        var normalized = KmdfDeviceManager.NormalizePortName(visiblePort);
        return $@"\\.\VComTunnelCtl_{normalized}";
    }

    private static SafeFileHandle OpenDriver(string visiblePort)
    {
        var attempted = new[] { BuildControlDevicePath(visiblePort), @"\\.\VComTunnelCtl0" };
        foreach (var path in attempted)
        {
            var handle = CreateFileW(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Could not open KMDF control channel for {visiblePort}. Tried: {string.Join(", ", attempted)}.");
    }

    private void Attach()
    {
        var input = new byte[136];
        WriteUInt16(input, 0, ProtocolMajor);
        WriteUInt16(input, 2, ProtocolMinor);
        var instance = Encoding.Unicode.GetBytes(Environment.MachineName);
        Array.Copy(instance, 0, input, 8, Math.Min(instance.Length, 126));

        var output = new byte[80];
        DeviceIoControlChecked(_eventDriver, IoctlAttach, input, output);
        var driverMajor = ReadUInt16(output, 0);
        var driverMinor = ReadUInt16(output, 2);
        if (driverMajor != ProtocolMajor || driverMinor < ProtocolMinor)
        {
            throw new InvalidOperationException($"KMDF driver protocol {driverMajor}.{driverMinor} is older than required {ProtocolMajor}.{ProtocolMinor}. Rebuild and reinstall VComTunnel.Serial.");
        }
    }

    private async Task EventLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            var output = new byte[MaxEventBytes];

            while (!_stop.IsCancellationRequested)
            {
                var bytes = DeviceIoControlChecked(_eventDriver, IoctlWaitEvent, null, output);
                if (bytes < EventHeaderSize)
                {
                    throw new InvalidOperationException("KMDF driver returned a truncated event.");
                }

                var size = ReadUInt32(output, 0);
                var type = ReadUInt16(output, 8);
                if (size < EventHeaderSize || size > bytes)
                {
                    throw new InvalidOperationException($"KMDF driver returned an invalid event frame type={type}, size={size}, bytes={bytes}.");
                }

                var payloadBytes = checked((int)size - EventHeaderSize);
                var frame = BuildNetworkFrame(type, output, EventHeaderSize, payloadBytes);
                if (frame.Bytes.Length > 0)
                {
                    await SendFrameWithAckAsync(stream, frame).ConfigureAwait(false);
                }
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
            var buffer = new byte[MaxRxBytes];

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
                    _log.Info(_mapping.Name, $"RFC2217 notification command {notification.Command} ({notification.Payload.Length} byte(s)).");
                }

                if (frame.SerialData.Length == 0)
                {
                    continue;
                }

                var push = new byte[8 + frame.SerialData.Length];
                WriteUInt32(push, 0, 0);
                WriteUInt32(push, 4, (uint)frame.SerialData.Length);
                Buffer.BlockCopy(frame.SerialData, 0, push, 8, frame.SerialData.Length);
                _log.Info(_mapping.Name, $"RFC2217 RX serial {frame.SerialData.Length} byte(s).");
                await PushRxWithBackpressureAsync(stream, push, frame.SerialData.Length).ConfigureAwait(false);
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

    private void Fault(Exception ex)
    {
        LastError = ex.Message;
        State = TunnelRunState.Faulted;
        SetConnectionState(3);
        _log.Error(_mapping.Name, $"KMDF tunnel faulted: {ex.Message}");
        _faulted(this, ex.Message);
        Dispose();
    }

    private void SetConnectionState(uint state)
    {
        if (_commandDriver is null || _commandDriver.IsInvalid || _commandDriver.IsClosed)
        {
            return;
        }

        var input = new byte[4];
        WriteUInt32(input, 0, state);
        DeviceIoControl(_commandDriver, IoctlSetConnectionState, input, input.Length, null, 0, out _, IntPtr.Zero);
    }

    private void ApplyNotification(Rfc2217Notification notification)
    {
        if (Rfc2217Client.IsCommandAck(notification.Command))
        {
            var pendingAck = CompletePendingAck(notification);
            if (pendingAck == PendingAckResult.CompletedWithAcceptedSerialSetting)
            {
                ApplyRemoteSerialSetting(notification);
                return;
            }

            if (pendingAck == PendingAckResult.CompletedWithAcceptedSetControl)
            {
                _log.Warn(_mapping.Name, $"RFC2217 peer accepted SET-CONTROL {DescribeSetControlNotification(notification)}.");
                return;
            }

            if (pendingAck != PendingAckResult.NotPending)
            {
                return;
            }

            if (ApplyRemoteSerialSetting(notification))
            {
                return;
            }

            if (ApplyRemoteControlState(notification))
            {
                return;
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
            var input = new byte[8];
            WriteUInt32(input, 0, Rfc2217Client.MapNotifyModemStateToWindowsStatus(notification.Payload[0]));
            WriteUInt32(input, 4, Rfc2217Client.MapNotifyModemStateToWindowsEvents(notification.Payload[0]));
            DeviceIoControlChecked(_commandDriver, IoctlSetModemState, input, null);
        }
        else if (notification.Command == Rfc2217Client.NotifyLineState)
        {
            var input = new byte[8];
            WriteUInt32(input, 0, Rfc2217Client.MapNotifyLineStateToWindowsErrors(notification.Payload[0]));
            WriteUInt32(input, 4, Rfc2217Client.MapNotifyLineStateToWindowsEvents(notification.Payload[0]));
            DeviceIoControlChecked(_commandDriver, IoctlSetLineState, input, null);
        }
    }

    private void ApplyTelnetOption(Rfc2217TelnetOptionEvent option)
    {
        if (option.Option == Rfc2217Client.TelnetOptionComPortControl)
        {
            if (option.Rejected)
            {
                throw new IOException($"RFC2217 endpoint rejected Telnet COM-PORT-OPTION ({option.Describe()}). This endpoint is not exposing RFC2217 serial-control negotiation.");
            }

            _log.Info(_mapping.Name, $"RFC2217 Telnet negotiation {option.Describe()}.");
            return;
        }

        if (option.Option is Rfc2217Client.TelnetOptionBinary or Rfc2217Client.TelnetOptionSuppressGoAhead)
        {
            if (option.Rejected)
            {
                _log.Warn(_mapping.Name, $"RFC2217 Telnet negotiation {option.Describe()}; continuing with degraded Telnet compatibility.");
            }
            else
            {
                _log.Info(_mapping.Name, $"RFC2217 Telnet negotiation {option.Describe()}.");
            }
        }
    }

    private bool ApplyRemoteSerialSetting(Rfc2217Notification notification)
    {
        if (notification.Command == Rfc2217Client.AckSetBaudRate)
        {
            if (notification.Payload.Length != 4)
            {
                return false;
            }

            var baudRate = Rfc2217Client.ReadUInt32Payload(notification.Payload);
            if (baudRate == 0)
            {
                return false;
            }

            var input = new byte[12];
            WriteUInt32(input, 0, RemoteBaudRate);
            WriteUInt32(input, 4, baudRate);
            DeviceIoControlChecked(_commandDriver, IoctlSetRemoteSettings, input, null);
            return true;
        }

        if (notification.Payload.Length != 1 || notification.Payload[0] == 0)
        {
            return false;
        }

        var lineInput = new byte[12];
        switch (notification.Command)
        {
            case Rfc2217Client.AckSetDataSize:
                WriteUInt32(lineInput, 0, RemoteWordLength);
                lineInput[10] = notification.Payload[0];
                break;
            case Rfc2217Client.AckSetParity:
                WriteUInt32(lineInput, 0, RemoteParity);
                lineInput[9] = Rfc2217Client.MapRfc2217ParityToWindows(notification.Payload[0]);
                break;
            case Rfc2217Client.AckSetStopSize:
                WriteUInt32(lineInput, 0, RemoteStopBits);
                lineInput[8] = Rfc2217Client.MapRfc2217StopBitsToWindows(notification.Payload[0]);
                break;
            default:
                return false;
        }

        DeviceIoControlChecked(_commandDriver, IoctlSetRemoteSettings, lineInput, null);
        return true;
    }

    private bool ApplyRemoteControlState(Rfc2217Notification notification)
    {
        if (notification.Command != Rfc2217Client.AckSetControl || notification.Payload.Length != 1)
        {
            return false;
        }

        _log.Info(_mapping.Name, $"RFC2217 remote control state {DescribeSetControlNotification(notification)}.");
        return true;
    }

    private static string DescribeSetControlNotification(Rfc2217Notification notification)
    {
        return notification.Payload.Length == 1
            ? $"{notification.Payload[0]} ({Rfc2217Client.DescribeSetControlValue(notification.Payload[0])})"
            : $"{notification.Payload.Length} byte(s)";
    }

    private async Task SendFrameWithAckAsync(NetworkStream stream, Rfc2217OutboundFrame frame)
    {
        if (frame.ExpectedAckCommands.Length == 0)
        {
            await WaitForRemoteFlowAsync().ConfigureAwait(false);
            await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var wait = PrepareAckWait(frame.ExpectedAckCommands);
            await WaitForRemoteFlowAsync().ConfigureAwait(false);
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

    private async Task PushRxWithBackpressureAsync(NetworkStream stream, byte[] push, int byteCount)
    {
        var suspended = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (TryDeviceIoControl(_commandDriver, IoctlPushRx, push, null, out var error))
                {
                    _log.Info(_mapping.Name, $"Driver RX push completed {byteCount} byte(s).");
                    return;
                }

                if (!IsRxBackpressureError(error))
                {
                    throw new Win32Exception(error);
                }

                if (!suspended)
                {
                    await WriteNetworkAsync(stream, Rfc2217Client.BuildLocalFlowControlSuspend()).ConfigureAwait(false);
                    suspended = true;
                    _log.Warn(_mapping.Name, $"Driver RX push returned backpressure error {error}; sent RFC2217 FLOWCONTROL-SUSPEND.");
                }

                await Task.Delay(RxBackpressureRetryInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (suspended && !_stop.IsCancellationRequested)
            {
                await WriteNetworkAsync(stream, Rfc2217Client.BuildLocalFlowControlResume()).ConfigureAwait(false);
                _log.Info(_mapping.Name, "Driver RX buffer accepted data; sent RFC2217 FLOWCONTROL-RESUME.");
            }
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
            else if ((index = _pendingAcks.FindIndex(expected => expected.MatchesAcceptedValue(notification))) >= 0)
            {
                result = PendingAckResult.CompletedWithAcceptedSerialSetting;
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

    private bool IsRemoteFlowSuspended()
    {
        lock (_flowLock)
        {
            return _remoteFlowSuspended;
        }
    }

    private void MarkNetworkActivity()
    {
        Interlocked.Exchange(ref _lastNetworkActivityTicks, Stopwatch.GetTimestamp());
    }

    private Rfc2217OutboundFrame BuildNetworkFrame(ushort type, byte[] buffer, int offset, int length)
    {
        switch (type)
        {
            case EventTypeTxData:
                _log.Info(_mapping.Name, $"KMDF TX event {length} byte(s).");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.EscapeSerialData(buffer, offset, length),
                    [],
                    "serial data");

            case EventTypeSetBaudRate:
                EnsurePayload(type, length, 4);
                var baudRate = ReadUInt32(buffer, offset);
                _log.Info(_mapping.Name, $"RFC2217 set baud {baudRate}.");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.BuildSetBaudRate(baudRate),
                    [],
                    "baud-rate");

            case EventTypeSetLineControl:
                EnsurePayload(type, length, 4);
                var stopBits = buffer[offset];
                var parity = buffer[offset + 1];
                var wordLength = buffer[offset + 2];
                _log.Info(_mapping.Name, $"RFC2217 set line data={wordLength}, parity={parity}, stop={stopBits}.");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.BuildSetLineControl(stopBits, parity, wordLength),
                    [],
                    "line-control");

            case EventTypeSetModemControl:
                EnsurePayload(type, length, 8);
                var mask = ReadUInt32(buffer, offset);
                bool? dtr = (mask & ModemControlDtr) != 0 ? buffer[offset + 4] != 0 : null;
                bool? rts = (mask & ModemControlRts) != 0 ? buffer[offset + 5] != 0 : null;
                if (_suppressInitialControlLineSync && !_initialControlLineSyncHandled)
                {
                    _initialControlLineSyncHandled = true;
                    _log.Info(_mapping.Name, $"Suppressed initial modem-control sync dtr={dtr?.ToString() ?? "-"}, rts={rts?.ToString() ?? "-"}.");
                    return new Rfc2217OutboundFrame([], [], "initial modem-control sync suppressed");
                }

                _initialControlLineSyncHandled = true;
                _log.Info(_mapping.Name, $"RFC2217 set modem dtr={dtr?.ToString() ?? "-"}, rts={rts?.ToString() ?? "-"}.");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.BuildSetModemControl(dtr, rts),
                    [],
                    "modem-control");

            case EventTypeSetHandflow:
                EnsurePayload(type, length, 8);
                var controlHandshake = ReadUInt32(buffer, offset);
                var flowReplace = ReadUInt32(buffer, offset + 4);
                _log.Info(_mapping.Name, $"RFC2217 set handflow control=0x{controlHandshake:X8}, flow=0x{flowReplace:X8}.");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.BuildSetHandflow(controlHandshake, flowReplace),
                    [],
                    "handflow");

            case EventTypeSetBreak:
                EnsurePayload(type, length, 4);
                var breakEnabled = buffer[offset] != 0;
                _log.Info(_mapping.Name, $"RFC2217 set break {breakEnabled}.");
                return new Rfc2217OutboundFrame(
                    Rfc2217Client.BuildSetBreak(breakEnabled),
                    [],
                    "break");

            case EventTypePurge:
                EnsurePayload(type, length, 4);
                var purgeMask = ReadUInt32(buffer, offset);
                _log.Info(_mapping.Name, $"RFC2217 purge 0x{purgeMask:X8}.");
                var purgeFrame = Rfc2217Client.BuildPurge(purgeMask);
                return new Rfc2217OutboundFrame(
                    purgeFrame,
                    [],
                    "purge");

            case EventTypeLocalFlowControl:
                EnsurePayload(type, length, 4);
                var suspend = buffer[offset] != 0;
                var flowAction = _localFlowControl.Apply(suspend);
                _log.Info(_mapping.Name, $"RFC2217 local flow-control {(suspend ? "suspend" : "resume")} depth={_localFlowControl.SuspendDepth}.");
                return new Rfc2217OutboundFrame(
                    flowAction switch
                    {
                        Rfc2217LocalFlowControlAction.Suspend => Rfc2217Client.BuildLocalFlowControlSuspend(),
                        Rfc2217LocalFlowControlAction.Resume => Rfc2217Client.BuildLocalFlowControlResume(),
                        _ => []
                    },
                    [],
                    flowAction switch
                    {
                        Rfc2217LocalFlowControlAction.Suspend => "local-flow-control-suspend",
                        Rfc2217LocalFlowControlAction.Resume => "local-flow-control-resume",
                        _ => "local-flow-control-coalesced"
                    });

            default:
                throw new InvalidOperationException($"KMDF driver returned unsupported event type {type}.");
        }
    }

    private static void EnsurePayload(ushort type, int actualLength, int minimumLength)
    {
        if (actualLength < minimumLength)
        {
            throw new InvalidOperationException($"KMDF event {type} payload was truncated: {actualLength}/{minimumLength} byte(s).");
        }
    }

    private static int DeviceIoControlChecked(SafeFileHandle? driver, uint ioctl, byte[]? input, byte[]? output)
    {
        if (driver is null)
        {
            throw new InvalidOperationException("KMDF driver is not open.");
        }

        var ok = DeviceIoControl(
            driver,
            ioctl,
            input,
            input?.Length ?? 0,
            output,
            output?.Length ?? 0,
            out var bytesReturned,
            IntPtr.Zero);

        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return bytesReturned;
    }

    private static bool TryDeviceIoControl(SafeFileHandle? driver, uint ioctl, byte[]? input, byte[]? output, out int error)
    {
        if (driver is null)
        {
            throw new InvalidOperationException("KMDF driver is not open.");
        }

        var ok = DeviceIoControl(
            driver,
            ioctl,
            input,
            input?.Length ?? 0,
            output,
            output?.Length ?? 0,
            out _,
            IntPtr.Zero);

        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    private static bool IsRxBackpressureError(int error)
    {
        return error is ErrorBufferOverflow or ErrorInsufficientBuffer or ErrorMoreData;
    }

    private static uint CtlCode(uint function)
    {
        const uint deviceType = 0x8000;
        const uint methodBuffered = 0;
        const uint fileReadWrite = 3;
        return (deviceType << 16) | (fileReadWrite << 14) | (function << 2) | methodBuffered;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) => BitConverter.ToUInt16(buffer, offset);
    private static uint ReadUInt32(byte[] buffer, int offset) => BitConverter.ToUInt32(buffer, offset);
    private static void WriteUInt16(byte[] buffer, int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
    private static void WriteUInt32(byte[] buffer, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private enum PendingAckResult
    {
        NotPending,
        Completed,
        CompletedWithAcceptedSerialSetting,
        CompletedWithAcceptedSetControl,
        Failed
    }

    private sealed record Rfc2217OutboundFrame(
        byte[] Bytes,
        Rfc2217ExpectedAck[] ExpectedAckCommands,
        string Description,
        bool ContinueOnAckTimeout = false);

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
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
