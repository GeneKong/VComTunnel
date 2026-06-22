using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace VComTunnel.Core;

public sealed class Com0comServiceTunnelSession : IManagedTunnelSession
{
    private const int MaxFrameBytes = 4096;
    private const int SerialRxWriteChunkBytes = 256;
    private const int SerialRxQueueCapacityChunks = 256;
    private const int SerialRxQueueWarnBytes = 32 * 1024;

    private static readonly TimeSpan SerialModemPollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan SerialSettingsPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CommandAckTimeout = Rfc2217Client.RecommendedCommandAckTimeout;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAlivePollInterval = TimeSpan.FromSeconds(5);

    private readonly TunnelMapping _mapping;
    private readonly InMemoryLog _log;
    private readonly Action<IManagedTunnelSession, string> _faulted;
    private readonly ISerialPortEndpointFactory _serialPorts;
    private readonly Rfc2217Client _rfc2217 = new();
    private readonly EspToolBaudRateMonitor _espToolBaudRate = new();
    private readonly object _ackLock = new();
    private readonly List<Rfc2217ExpectedAck> _pendingAcks = [];
    private readonly object _flowLock = new();
    private readonly SemaphoreSlim _networkWriteLock = new(1, 1);
    private readonly Channel<SerialRxChunk> _serialRxQueue = Channel.CreateBounded<SerialRxChunk>(new BoundedChannelOptions(SerialRxQueueCapacityChunks)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ByteThroughputLogThrottle _serialTxLog = new(TimeSpan.FromSeconds(1));
    private readonly ByteThroughputLogThrottle _serialRxLog = new(TimeSpan.FromSeconds(1));
    private readonly object _serialStateLock = new();
    private readonly CancellationTokenSource _stop = new();
    private int _forwardControlLines;
    private TaskCompletionSource? _pendingAckCompletion;
    private TaskCompletionSource? _flowResumeCompletion;
    private bool _remoteFlowSuspended;
    private long _lastNetworkActivityTicks;
    private int _serialRxQueuedBytes;
    private long _serialRxQueueBackpressureLogTicks;
    private ISerialPortEndpoint? _serial;
    private TcpClient? _tcp;
    private Task? _serialLoop;
    private Task? _serialModemLoop;
    private Task? _serialSettingsLoop;
    private Task? _networkLoop;
    private Task? _serialRxLoop;
    private Task? _keepAliveLoop;
    private SerialPortSnapshot? _lastSerialSnapshot;
    private bool _pollModemState;
    private int _disposed;

    public Com0comServiceTunnelSession(
        TunnelMapping mapping,
        InMemoryLog log,
        Action<IManagedTunnelSession, string> faulted,
        ISerialPortEndpointFactory? serialPorts = null)
    {
        _mapping = mapping;
        _forwardControlLines = mapping.Hub4comForwardControlLines ? 1 : 0;
        _log = log;
        _faulted = faulted;
        _serialPorts = serialPorts ?? new Win32SerialPortEndpointFactory();
        State = TunnelRunState.Starting;
        MarkNetworkActivity();
    }

    public TunnelRunState State { get; private set; }
    public string? LastError { get; private set; }

    private bool ForwardControlLines => Volatile.Read(ref _forwardControlLines) != 0;

    public void UpdateMapping(TunnelMapping mapping)
    {
        Interlocked.Exchange(ref _forwardControlLines, mapping.Hub4comForwardControlLines ? 1 : 0);
    }

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
        TunnelTcpOptions.ConfigureLowLatency(_tcp);
        try
        {
            await _tcp.ConnectAsync(_mapping.Host, _mapping.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new IOException($"Could not connect to RFC2217 endpoint {_mapping.Host}:{_mapping.Port}: {ex.Message}", ex);
        }

        var stream = _tcp.GetStream();
        _serialRxLoop = Task.Run(SerialRxWriteLoopAsync);
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

        lock (_serialStateLock)
        {
            _lastSerialSnapshot = _serial.GetSnapshot();
        }

        _pollModemState = !_serial.SupportsModemStatusEvents;
        if (_pollModemState)
        {
            _log.Warn(_mapping.Name, $"Serial modem-control events are unavailable; falling back to dedicated {SerialModemPollInterval.TotalMilliseconds:0} ms polling for DTR/RTS.");
            _serialModemLoop = Task.Run(SerialModemPollingLoopAsync);
        }
        else
        {
            _log.Info(_mapping.Name, "Serial modem-control events enabled through overlapped WaitCommEvent for DTR/RTS.");
            _serialModemLoop = Task.Run(SerialModemEventLoopAsync);
        }

        _serialLoop = Task.Run(SerialLoopAsync);
        _serialSettingsLoop = Task.Run(SerialSettingsLoopAsync);
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
        _serialRxQueue.Writer.TryComplete();
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

                var requestedBaudRate = _espToolBaudRate.ObserveOutbound(buffer, 0, read);
                if (requestedBaudRate is not null)
                {
                    _log.Info(_mapping.Name, $"Detected esptool baud-rate change request {requestedBaudRate.Value}.");
                }

                await WaitForRemoteFlowAsync().ConfigureAwait(false);
                if (Rfc2217Client.RequiresSerialDataEscaping(buffer, 0, read))
                {
                    await WriteNetworkAsync(stream, Rfc2217Client.EscapeSerialData(buffer, 0, read)).ConfigureAwait(false);
                }
                else
                {
                    await WriteNetworkAsync(stream, buffer.AsMemory(0, read)).ConfigureAwait(false);
                }

                LogSerialTx(read);
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private async Task SerialModemEventLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var eventMask = _serial!.WaitForModemStatusChange(_stop.Token);
                var currentModemStatus = _serial.GetModemStatus();
                var frame = UpdateSerialModemState(currentModemStatus, eventMask);
                if (frame.Bytes.Length > 0)
                {
                    await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);
                    _log.Info(_mapping.Name, frame.Description);
                }
                else if (frame.LogWhenEmpty)
                {
                    _log.Info(_mapping.Name, frame.Description);
                }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private async Task SerialModemPollingLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var currentModemStatus = _serial!.GetModemStatus();
                var frame = UpdateSerialModemState(currentModemStatus, SerialPortSnapshot.EventNone);
                if (frame.Bytes.Length > 0)
                {
                    await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);
                    _log.Info(_mapping.Name, frame.Description);
                }
                else if (frame.LogWhenEmpty)
                {
                    _log.Info(_mapping.Name, frame.Description);
                }

                await Task.Delay(SerialModemPollInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }
    private async Task SerialSettingsLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var frame = UpdateSerialSettings(_serial!.GetSettings());
                if (frame.Bytes.Length > 0)
                {
                    await WriteNetworkAsync(stream, frame.Bytes).ConfigureAwait(false);
                    _log.Info(_mapping.Name, frame.Description);
                }
                else if (frame.LogWhenEmpty)
                {
                    _log.Info(_mapping.Name, frame.Description);
                }

                await Task.Delay(SerialSettingsPollInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private Rfc2217OutboundFrame UpdateSerialStateFromPoll(SerialPortSnapshot current)
    {
        lock (_serialStateLock)
        {
            if (_lastSerialSnapshot is not { } previous)
            {
                _lastSerialSnapshot = current;
                return new Rfc2217OutboundFrame([], [], "serial state baseline");
            }

            _lastSerialSnapshot = current;
            return BuildStateChangeFrame(previous, current, SerialPortSnapshot.EventNone, ForwardControlLines);
        }
    }

    private Rfc2217OutboundFrame UpdateSerialSettings(SerialPortSettings settings)
    {
        lock (_serialStateLock)
        {
            if (_lastSerialSnapshot is not { } previous)
            {
                _lastSerialSnapshot = new SerialPortSnapshot(0, settings.BaudRate, settings.ByteSize, settings.Parity, settings.StopBits);
                return new Rfc2217OutboundFrame([], [], "serial settings baseline");
            }

            var current = previous with
            {
                BaudRate = settings.BaudRate,
                ByteSize = settings.ByteSize,
                Parity = settings.Parity,
                StopBits = settings.StopBits
            };
            _lastSerialSnapshot = current;
            return BuildSettingsChangeFrame(previous, current);
        }
    }

    private Rfc2217OutboundFrame UpdateSerialModemState(uint currentModemStatus, uint eventMask)
    {
        lock (_serialStateLock)
        {
            if (_lastSerialSnapshot is not { } previous)
            {
                _lastSerialSnapshot = new SerialPortSnapshot(currentModemStatus, 0, 0, 0, 0);
                return new Rfc2217OutboundFrame([], [], "serial modem baseline");
            }

            var current = previous with { ModemStatus = currentModemStatus };
            _lastSerialSnapshot = current;
            return BuildModemChangeFrame(previous, current, eventMask, ForwardControlLines);
        }
    }

    private static Rfc2217OutboundFrame BuildStateChangeFrame(SerialPortSnapshot previous, SerialPortSnapshot current, bool forwardControlLines)
    {
        return BuildStateChangeFrame(previous, current, SerialPortSnapshot.EventNone, forwardControlLines);
    }

    private static Rfc2217OutboundFrame BuildStateChangeFrame(SerialPortSnapshot previous, SerialPortSnapshot current, uint eventMask, bool forwardControlLines)
    {
        return CombineFrames(
            BuildSettingsChangeFrame(previous, current),
            BuildModemChangeFrame(previous, current, eventMask, forwardControlLines));
    }

    private static Rfc2217OutboundFrame BuildSettingsChangeFrame(SerialPortSnapshot previous, SerialPortSnapshot current)
    {
        var frames = new List<byte[]>();
        var descriptions = new List<string>();

        if (previous.BaudRate != current.BaudRate && current.BaudRate > 0)
        {
            frames.Add(Rfc2217Client.BuildSetBaudRate(current.BaudRate));
            descriptions.Add($"baud={current.BaudRate}");
        }

        if (previous.ByteSize != current.ByteSize
            || previous.Parity != current.Parity
            || previous.StopBits != current.StopBits)
        {
            frames.Add(Rfc2217Client.BuildSetLineControl(current.StopBits, current.Parity, current.ByteSize));
            descriptions.Add($"line data={current.ByteSize}, parity={current.Parity}, stop={current.StopBits}");
        }

        return BuildFrame(frames, descriptions);
    }

    private static Rfc2217OutboundFrame BuildModemChangeFrame(SerialPortSnapshot previous, SerialPortSnapshot current, uint eventMask, bool forwardControlLines)
    {
        var frames = new List<byte[]>();
        var descriptions = new List<string>();
        var previousDtr = MapCom0comPeerDtr(previous.ModemStatus);
        var currentDtr = MapCom0comPeerDtr(current.ModemStatus);
        var previousRts = MapCom0comPeerRts(previous.ModemStatus);
        var currentRts = MapCom0comPeerRts(current.ModemStatus);
        bool? dtr = previousDtr != currentDtr ? currentDtr : null;
        bool? rts = previousRts != currentRts ? currentRts : null;
        var modemChanged = dtr is not null || rts is not null;
        var missedDtrPulse = !modemChanged && (eventMask & SerialPortSnapshot.EventDsr) != 0 && previousDtr == currentDtr;
        var missedRtsPulse = !modemChanged && (eventMask & SerialPortSnapshot.EventCts) != 0 && previousRts == currentRts;

        if (!forwardControlLines)
        {
            if (modemChanged || missedDtrPulse || missedRtsPulse)
            {
                bool? suppressedDtr = modemChanged ? dtr : missedDtrPulse ? !currentDtr : null;
                bool? suppressedRts = modemChanged ? rts : missedRtsPulse ? !currentRts : null;
                return new Rfc2217OutboundFrame(
                    [],
                    [],
                    $"Suppressed modem-control because control-line forwarding is disabled raw=0x{previous.ModemStatus:X8}->0x{current.ModemStatus:X8}, event=0x{eventMask:X8}, dtr={FormatNullableBool(suppressedDtr)}, rts={FormatNullableBool(suppressedRts)}.",
                    LogWhenEmpty: true);
            }

            return BuildFrame(frames, descriptions);
        }

        if (modemChanged)
        {
            frames.Add(Rfc2217Client.BuildSetModemControl(dtr, rts));
            descriptions.Add($"TX modem-control raw=0x{previous.ModemStatus:X8}->0x{current.ModemStatus:X8}, dtr={FormatNullableBool(dtr)}, rts={FormatNullableBool(rts)}");
        }
        else if (missedDtrPulse || missedRtsPulse)
        {
            var pulseDtr = missedDtrPulse ? !currentDtr : (bool?)null;
            var pulseRts = missedRtsPulse ? !currentRts : (bool?)null;
            var restoreDtr = missedDtrPulse ? currentDtr : (bool?)null;
            var restoreRts = missedRtsPulse ? currentRts : (bool?)null;
            frames.Add(Rfc2217Client.BuildSetModemControl(pulseDtr, pulseRts));
            frames.Add(Rfc2217Client.BuildSetModemControl(restoreDtr, restoreRts));
            descriptions.Add($"TX modem-control synthesized-pulse raw=0x{current.ModemStatus:X8}, event=0x{eventMask:X8}, dtr={FormatNullableBool(pulseDtr)}, rts={FormatNullableBool(pulseRts)}");
            descriptions.Add($"TX modem-control synthesized-restore raw=0x{current.ModemStatus:X8}, event=0x{eventMask:X8}, dtr={FormatNullableBool(restoreDtr)}, rts={FormatNullableBool(restoreRts)}");
        }

        return BuildFrame(frames, descriptions);
    }

    private static Rfc2217OutboundFrame CombineFrames(params Rfc2217OutboundFrame[] frames)
    {
        var nonEmpty = frames.Where(frame => frame.Bytes.Length > 0).ToArray();
        if (nonEmpty.Length == 0)
        {
            return new Rfc2217OutboundFrame([], [], "serial state unchanged");
        }

        return new Rfc2217OutboundFrame(
            nonEmpty.SelectMany(frame => frame.Bytes).ToArray(),
            [],
            $"RFC2217 serial state {string.Join("; ", nonEmpty.Select(frame => frame.Description))} sent without ACK wait.");
    }

    private static Rfc2217OutboundFrame BuildFrame(List<byte[]> frames, List<string> descriptions)
    {
        return frames.Count == 0
            ? new Rfc2217OutboundFrame([], [], "serial state unchanged")
            : new Rfc2217OutboundFrame(
                frames.SelectMany(frame => frame).ToArray(),
                [],
                string.Join("; ", descriptions));
    }

    private static string FormatNullableBool(bool? value) => value?.ToString() ?? "-";

    public static bool MapCom0comPeerDtr(uint modemStatus) => (modemStatus & SerialPortSnapshot.Dsr) != 0;

    public static bool MapCom0comPeerRts(uint modemStatus) => (modemStatus & SerialPortSnapshot.Cts) != 0;

    public static byte[] BuildCom0comPeerModemControlFrames(uint previousModemStatus, uint currentModemStatus, uint eventMask, bool forwardControlLines = true)
    {
        var previous = new SerialPortSnapshot(previousModemStatus, 0, 0, 0, 0);
        var current = previous with { ModemStatus = currentModemStatus };
        return BuildModemChangeFrame(previous, current, eventMask, forwardControlLines).Bytes;
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
                    var confirmedBaudRate = _espToolBaudRate.ObserveInbound(frame.SerialData, 0, frame.SerialData.Length);
                    if (confirmedBaudRate is not null)
                    {
                        await SendFrameWithAckAsync(
                            stream,
                            new Rfc2217OutboundFrame(
                                Rfc2217Client.BuildSetBaudRate(confirmedBaudRate.Value),
                                [],
                                $"esptool baud-rate {confirmedBaudRate.Value}")).ConfigureAwait(false);
                        _log.Info(_mapping.Name, $"RFC2217 esptool baud-rate {confirmedBaudRate.Value} sent after target response.");
                    }

                    await EnqueueSerialRxAsync(frame.SerialData).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            Fault(ex);
        }
    }

    private async Task EnqueueSerialRxAsync(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(SerialRxWriteChunkBytes, bytes.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(bytes, offset, chunk, 0, count);
            var queuedBytes = Interlocked.Add(ref _serialRxQueuedBytes, count);
            var waitStarted = Stopwatch.GetTimestamp();
            try
            {
                await _serialRxQueue.Writer.WriteAsync(new SerialRxChunk(chunk), _stop.Token).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref _serialRxQueuedBytes, -count);
                throw;
            }

            var waited = Stopwatch.GetElapsedTime(waitStarted);
            if (waited > TimeSpan.FromMilliseconds(4) || queuedBytes >= SerialRxQueueWarnBytes)
            {
                LogSerialRxQueueBackpressure(queuedBytes, waited);
            }

            offset += count;
        }
    }

    private async Task SerialRxWriteLoopAsync()
    {
        try
        {
            await foreach (var chunk in _serialRxQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await _serial!.WriteAsync(chunk.Bytes, _stop.Token, LogSerialRxBackpressure).ConfigureAwait(false);
                    LogSerialRx(chunk.Bytes.Length);
                }
                finally
                {
                    Interlocked.Add(ref _serialRxQueuedBytes, -chunk.Bytes.Length);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
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

            if (notification.Command == Rfc2217Client.AckSetControl && notification.Payload.Length == 1)
            {
                _log.Info(_mapping.Name, $"RFC2217 remote control state {DescribeSetControlNotification(notification)}.");
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

    private async Task WriteNetworkAsync(NetworkStream stream, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

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

    private void LogSerialTx(int bytes)
    {
        _serialTxLog.Record(bytes, (totalBytes, chunks) =>
            _log.Info(_mapping.Name, $"Serial TX summary since last log: {totalBytes} byte(s) in {chunks} chunk(s) from {_mapping.BackingPort}."));
    }

    private void LogSerialRx(int bytes)
    {
        _serialRxLog.Record(bytes, (totalBytes, chunks) =>
            _log.Info(_mapping.Name, $"RFC2217 RX serial summary since last log: {totalBytes} byte(s) in {chunks} chunk(s) to {_mapping.BackingPort}."));
    }

    private void LogSerialRxBackpressure(SerialPortBackpressureInfo info)
    {
        _log.Warn(
            _mapping.Name,
            $"Serial RX backpressure on {_mapping.BackingPort}: no local-COM progress for {info.Duration.TotalMilliseconds:0} ms; holding {info.RemainingBytes}/{info.TotalBytes} byte(s) in the local-COM writer; RFC2217 reads continue until the bounded RX queue fills.");
    }

    private void LogSerialRxQueueBackpressure(int queuedBytes, TimeSpan waited)
    {
        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _serialRxQueueBackpressureLogTicks);
        if (next != 0 && now < next)
        {
            return;
        }

        Interlocked.Exchange(ref _serialRxQueueBackpressureLogTicks, now + Stopwatch.Frequency);
        _log.Warn(
            _mapping.Name,
            $"Serial RX queue backpressure on {_mapping.BackingPort}: queued {queuedBytes} byte(s), enqueue waited {waited.TotalMilliseconds:0.0} ms.");
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

    private bool ApplyRemoteSerialSetting(Rfc2217Notification notification)
    {
        var serial = _serial;
        if (serial is null)
        {
            return false;
        }

        SerialPortSnapshot previous;
        lock (_serialStateLock)
        {
            previous = _lastSerialSnapshot ?? serial.GetSnapshot();
        }

        if (!TryApplyRemoteSerialSetting(previous, notification, out var current))
        {
            return false;
        }

        serial.SetSettings(new SerialPortSettings(current.BaudRate, current.ByteSize, current.Parity, current.StopBits));
        lock (_serialStateLock)
        {
            var baseline = _lastSerialSnapshot ?? previous;
            _lastSerialSnapshot = current with { ModemStatus = baseline.ModemStatus };
        }

        _log.Info(_mapping.Name, $"RFC2217 remote serial setting accepted {Rfc2217ExpectedAck.Describe(notification)}.");
        return true;
    }

    private static bool TryApplyRemoteSerialSetting(SerialPortSnapshot previous, Rfc2217Notification notification, out SerialPortSnapshot current)
    {
        current = previous;
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

            current = previous with { BaudRate = baudRate };
            return true;
        }

        if (notification.Payload.Length != 1 || notification.Payload[0] == 0)
        {
            return false;
        }

        current = notification.Command switch
        {
            Rfc2217Client.AckSetDataSize => previous with { ByteSize = notification.Payload[0] },
            Rfc2217Client.AckSetParity => previous with { Parity = Rfc2217Client.MapRfc2217ParityToWindows(notification.Payload[0]) },
            Rfc2217Client.AckSetStopSize => previous with { StopBits = Rfc2217Client.MapRfc2217StopBitsToWindows(notification.Payload[0]) },
            _ => previous
        };
        return current != previous;
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
        CompletedWithAcceptedSerialSetting,
        CompletedWithAcceptedSetControl,
        Failed
    }

    private sealed record Rfc2217OutboundFrame(
        byte[] Bytes,
        Rfc2217ExpectedAck[] ExpectedAckCommands,
        string Description,
        bool ContinueOnAckTimeout = false,
        bool LogWhenEmpty = false);

    private sealed record SerialRxChunk(byte[] Bytes);


}

public interface ISerialPortEndpoint : IDisposable
{
    bool SupportsModemStatusEvents { get; }
    ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(byte[] bytes, CancellationToken cancellationToken, Action<SerialPortBackpressureInfo>? backpressure = null);
    SerialPortSnapshot GetSnapshot();
    SerialPortSettings GetSettings();
    void SetSettings(SerialPortSettings settings);
    uint GetModemStatus();
    uint WaitForModemStatusChange(CancellationToken cancellationToken);
}

public readonly record struct SerialPortSnapshot(
    uint ModemStatus,
    uint BaudRate,
    byte ByteSize,
    byte Parity,
    byte StopBits)
{
    public const uint Cts = 0x00000010;
    public const uint Dsr = 0x00000020;
    public const uint Ring = 0x00000040;
    public const uint Rlsd = 0x00000080;
    public const uint EventNone = 0x00000000;
    public const uint EventCts = 0x00000008;
    public const uint EventDsr = 0x00000010;
}

public readonly record struct SerialPortSettings(
    uint BaudRate,
    byte ByteSize,
    byte Parity,
    byte StopBits);

public sealed record SerialPortBackpressureInfo(int BytesWritten, int TotalBytes, TimeSpan Duration)
{
    public int RemainingBytes => Math.Max(0, TotalBytes - BytesWritten);
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


public sealed class EspToolBaudRateMonitor
{
    private const byte SlipEnd = 0xC0;
    private const byte SlipEsc = 0xDB;
    private const byte SlipEscEnd = 0xDC;
    private const byte SlipEscEsc = 0xDD;
    private const byte EspDirectionRequest = 0x00;
    private const byte EspDirectionResponse = 0x01;
    private const byte EspCommandChangeBaudRate = 0x0F;
    private const int EspPacketHeaderBytes = 8;
    private const int MaxSlipFrameBytes = 65536;

    private readonly object _lock = new();
    private readonly SlipDecodeState _outbound = new();
    private readonly SlipDecodeState _inbound = new();
    private uint? _pendingBaudRate;

    public uint? ObserveOutbound(byte[] buffer, int offset, int length)
    {
        lock (_lock)
        {
            uint? observed = null;
            foreach (var frame in Decode(_outbound, buffer, offset, length))
            {
                if (TryReadChangeBaudRequest(frame, out var baudRate))
                {
                    _pendingBaudRate = baudRate;
                    observed = baudRate;
                }
            }

            return observed;
        }
    }

    public uint? ObserveInbound(byte[] buffer, int offset, int length)
    {
        lock (_lock)
        {
            foreach (var frame in Decode(_inbound, buffer, offset, length))
            {
                if (IsChangeBaudResponse(frame) && _pendingBaudRate is not null)
                {
                    var baudRate = _pendingBaudRate.Value;
                    _pendingBaudRate = null;
                    return baudRate;
                }
            }

            return null;
        }
    }

    public static bool TryReadChangeBaudRequest(IReadOnlyList<byte> frame, out uint baudRate)
    {
        baudRate = 0;
        if (frame.Count < EspPacketHeaderBytes + sizeof(uint)
            || frame[0] != EspDirectionRequest
            || frame[1] != EspCommandChangeBaudRate)
        {
            return false;
        }

        var dataSize = frame[2] | (frame[3] << 8);
        if (dataSize < sizeof(uint) || frame.Count < EspPacketHeaderBytes + dataSize)
        {
            return false;
        }

        baudRate = ReadUInt32(frame, EspPacketHeaderBytes);
        return baudRate > 0;
    }

    private static bool IsChangeBaudResponse(IReadOnlyList<byte> frame)
    {
        return frame.Count >= 2
            && frame[0] == EspDirectionResponse
            && frame[1] == EspCommandChangeBaudRate;
    }

    private static uint ReadUInt32(IReadOnlyList<byte> bytes, int offset)
    {
        return (uint)(bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24));
    }

    private static IEnumerable<byte[]> Decode(SlipDecodeState state, byte[] buffer, int offset, int length)
    {
        var end = offset + length;
        for (var i = offset; i < end; i++)
        {
            var value = buffer[i];
            if (value == SlipEnd)
            {
                if (state.Frame.Count > 0)
                {
                    var completed = state.Frame.ToArray();
                    state.Reset();
                    yield return completed;
                }
                else
                {
                    state.Reset();
                }

                continue;
            }

            if (state.Escaped)
            {
                state.Escaped = false;
                value = value switch
                {
                    SlipEscEnd => SlipEnd,
                    SlipEscEsc => SlipEsc,
                    _ => value
                };
            }
            else if (value == SlipEsc)
            {
                state.Escaped = true;
                continue;
            }

            state.Frame.Add(value);
            if (state.Frame.Count > MaxSlipFrameBytes)
            {
                state.Reset();
            }
        }
    }

    private sealed class SlipDecodeState
    {
        public List<byte> Frame { get; } = [];
        public bool Escaped { get; set; }

        public void Reset()
        {
            Frame.Clear();
            Escaped = false;
        }
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
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint EventCts = 0x00000008;
    private const uint EventDsr = 0x00000010;
    private const uint DcbBinary = 0x00000001;
    private const uint DcbOutxCtsFlow = 0x00000004;
    private const uint DcbOutxDsrFlow = 0x00000008;
    private const uint DcbDsrSensitivity = 0x00000040;
    private const uint DcbOutX = 0x00000100;
    private const uint DcbInX = 0x00000200;
    private const uint DcbAbortOnError = 0x00004000;
    private const uint SerialReadFirstByteTimeoutMs = 2;
    private const uint SerialWriteTimeoutMs = 20;
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private static readonly TimeSpan WriteInitialBackpressureLogDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WriteBackpressureLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelOverlappedWaitTimeout = TimeSpan.FromSeconds(1);

    private readonly SafeFileHandle _handle;
    private readonly bool _supportsModemStatusEvents;
    private readonly object _readLock = new();
    private readonly object _writeLock = new();
    private readonly object _settingsLock = new();
    private readonly object _waitCommEventLock = new();

    private Win32SerialPortEndpoint(SafeFileHandle handle, bool supportsModemStatusEvents)
    {
        _handle = handle;
        _supportsModemStatusEvents = supportsModemStatusEvents;
    }

    public static Win32SerialPortEndpoint Open(string portName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("com0comService backend is only available on Windows.");
        }

        var path = BuildDevicePath(portName);
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOverlapped,
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
            ReadTotalTimeoutMultiplier = uint.MaxValue,
            ReadTotalTimeoutConstant = SerialReadFirstByteTimeoutMs,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = SerialWriteTimeoutMs
        };
        if (!SetCommTimeouts(handle, ref timeouts))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SerialPortOpenException(portName, path, error, "configure serial port timeouts for");
        }

        if (!ConfigureLowLatencyLocalHandflow(handle))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SerialPortOpenException(portName, path, error, "configure local serial handflow for");
        }

        var supportsModemStatusEvents = SetCommMask(handle, EventCts | EventDsr);
        return new Win32SerialPortEndpoint(handle, supportsModemStatusEvents);
    }

    private static string BuildDevicePath(string portName) => portName.StartsWith(@"\\.\", StringComparison.Ordinal)
        ? portName
        : $@"\\.\{portName}";

    private static bool ConfigureLowLatencyLocalHandflow(SafeFileHandle handle)
    {
        var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref dcb))
        {
            return false;
        }

        dcb.Flags = NormalizeLocalSerialFlags(dcb.Flags);
        return SetCommState(handle, ref dcb);
    }

    private static uint NormalizeLocalSerialFlags(uint flags)
    {
        flags |= DcbBinary;
        flags &= ~(DcbOutxCtsFlow
            | DcbOutxDsrFlow
            | DcbDsrSensitivity
            | DcbOutX
            | DcbInX
            | DcbAbortOnError);
        return flags;
    }

    public bool SupportsModemStatusEvents => _supportsModemStatusEvents;

    public ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_readLock)
        {
            return ValueTask.FromResult(ReadOverlapped(buffer, cancellationToken));
        }
    }

    public ValueTask WriteAsync(byte[] bytes, CancellationToken cancellationToken, Action<SerialPortBackpressureInfo>? backpressure = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_writeLock)
        {
            WriteOverlapped(bytes, cancellationToken, backpressure);
        }

        return ValueTask.CompletedTask;
    }

    public SerialPortSnapshot GetSnapshot()
    {
        var modemStatus = GetModemStatus();
        var settings = GetSettings();
        return new SerialPortSnapshot(modemStatus, settings.BaudRate, settings.ByteSize, settings.Parity, settings.StopBits);
    }

    public SerialPortSettings GetSettings()
    {
        lock (_settingsLock)
        {
            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            if (!GetCommState(_handle, ref dcb))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new SerialPortSettings(dcb.BaudRate, dcb.ByteSize, dcb.Parity, dcb.StopBits);
        }
    }

    public void SetSettings(SerialPortSettings settings)
    {
        lock (_settingsLock)
        {
            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            if (!GetCommState(_handle, ref dcb))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            dcb.BaudRate = settings.BaudRate;
            dcb.ByteSize = settings.ByteSize;
            dcb.Parity = settings.Parity;
            dcb.StopBits = settings.StopBits;
            dcb.Flags = NormalizeLocalSerialFlags(dcb.Flags);
            if (!SetCommState(_handle, ref dcb))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    public uint GetModemStatus()
    {
        if (!GetCommModemStatus(_handle, out var modemStatus))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return modemStatus;
    }

    public uint WaitForModemStatusChange(CancellationToken cancellationToken)
    {
        if (!SupportsModemStatusEvents)
        {
            throw new NotSupportedException("Serial modem status events are unavailable.");
        }

        lock (_waitCommEventLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WaitForModemStatusChangeOverlapped(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (!_handle.IsClosed && !_handle.IsInvalid)
        {
            _ = CancelIoEx(_handle, IntPtr.Zero);
        }

        _handle.Dispose();
    }

    private int ReadOverlapped(byte[] buffer, CancellationToken cancellationToken)
    {
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var wait = new ManualResetEvent(false);
        var overlappedPointer = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedNative>());
        var lifetime = new PendingOverlappedLifetime(pinned, wait, overlappedPointer);
        try
        {
            PrepareOverlapped(overlappedPointer, wait);
            if (!ReadFile(_handle, pinned.AddrOfPinnedObject(), buffer.Length, out var completedRead, overlappedPointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    throw new Win32Exception(error);
                }

                return checked((int)WaitForOverlappedResult(overlappedPointer, wait, cancellationToken, lifetime, logBackpressure: null));
            }

            return checked((int)completedRead);
        }
        finally
        {
            lifetime.DisposeIfCompleted();
        }
    }

    private void WriteOverlapped(byte[] bytes, CancellationToken cancellationToken, Action<SerialPortBackpressureInfo>? backpressure)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var keepPinnedAfterCancel = false;
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var wait = new ManualResetEvent(false);
                var overlappedPointer = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedNative>());
                var lifetime = new PendingOverlappedLifetime(null, wait, overlappedPointer);
                try
                {
                    PrepareOverlapped(overlappedPointer, wait);
                    var remaining = bytes.Length - offset;
                    if (!WriteFile(_handle, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), remaining, out var completedWritten, overlappedPointer))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorIoPending)
                        {
                            throw new Win32Exception(error);
                        }

                        completedWritten = WaitForOverlappedResult(
                            overlappedPointer,
                            wait,
                            cancellationToken,
                            lifetime,
                            () => backpressure?.Invoke(new SerialPortBackpressureInfo(offset, bytes.Length, Stopwatch.GetElapsedTime(lifetime.StartedTicks))));
                    }

                    if (completedWritten == 0)
                    {
                        break;
                    }

                    offset += checked((int)completedWritten);
                }
                finally
                {
                    if (lifetime.PendingAfterCancel)
                    {
                        keepPinnedAfterCancel = true;
                    }

                    lifetime.DisposeIfCompleted();
                }
            }
        }
        finally
        {
            if (!keepPinnedAfterCancel)
            {
                pinned.Free();
            }
        }
    }

    private uint WaitForModemStatusChangeOverlapped(CancellationToken cancellationToken)
    {
        var wait = new ManualResetEvent(false);
        var maskPointer = Marshal.AllocHGlobal(sizeof(uint));
        var overlappedPointer = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedNative>());
        var lifetime = new PendingOverlappedLifetime(null, wait, overlappedPointer, maskPointer);
        try
        {
            Marshal.WriteInt32(maskPointer, 0);
            PrepareOverlapped(overlappedPointer, wait);

            if (!WaitCommEvent(_handle, maskPointer, overlappedPointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new Win32Exception(error);
                }

                _ = WaitForOverlappedResult(overlappedPointer, wait, cancellationToken, lifetime, logBackpressure: null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return unchecked((uint)Marshal.ReadInt32(maskPointer));
        }
        finally
        {
            lifetime.DisposeIfCompleted();
        }
    }

    private static void PrepareOverlapped(IntPtr overlappedPointer, ManualResetEvent wait)
    {
        Marshal.StructureToPtr(
            new OverlappedNative { EventHandle = wait.SafeWaitHandle.DangerousGetHandle() },
            overlappedPointer,
            false);
    }

    private uint WaitForOverlappedResult(
        IntPtr overlappedPointer,
        ManualResetEvent wait,
        CancellationToken cancellationToken,
        PendingOverlappedLifetime lifetime,
        Action? logBackpressure)
    {
        var nextBackpressureAt = Stopwatch.GetTimestamp() + (long)(WriteInitialBackpressureLogDelay.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var completed = WaitHandle.WaitAny([wait, cancellationToken.WaitHandle], WriteInitialBackpressureLogDelay);
            if (completed == 0)
            {
                break;
            }

            if (completed == 1)
            {
                _ = CancelIoEx(_handle, overlappedPointer);
                if (!wait.WaitOne(CancelOverlappedWaitTimeout))
                {
                    lifetime.MarkPendingAfterCancel();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            var now = Stopwatch.GetTimestamp();
            if (logBackpressure is not null && now >= nextBackpressureAt)
            {
                logBackpressure();
                nextBackpressureAt = now + (long)(WriteBackpressureLogInterval.TotalSeconds * Stopwatch.Frequency);
            }
        }

        if (!GetOverlappedResult(_handle, overlappedPointer, out var transferred, bWait: false))
        {
            var resultError = Marshal.GetLastWin32Error();
            if (resultError == ErrorOperationAborted)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new Win32Exception(resultError);
        }

        return transferred;
    }

    private sealed class PendingOverlappedLifetime : IDisposable
    {
        private readonly GCHandle? _pinned;
        private readonly ManualResetEvent _wait;
        private readonly IntPtr _overlappedPointer;
        private readonly IntPtr _extraPointer;
        private bool _pendingAfterCancel;

        public PendingOverlappedLifetime(GCHandle? pinned, ManualResetEvent wait, IntPtr overlappedPointer, IntPtr extraPointer = default)
        {
            _pinned = pinned;
            _wait = wait;
            _overlappedPointer = overlappedPointer;
            _extraPointer = extraPointer;
            StartedTicks = Stopwatch.GetTimestamp();
        }

        public long StartedTicks { get; }

        public bool PendingAfterCancel => _pendingAfterCancel;

        public void MarkPendingAfterCancel() => _pendingAfterCancel = true;

        public void DisposeIfCompleted()
        {
            if (!_pendingAfterCancel)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_extraPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_extraPointer);
            }

            Marshal.FreeHGlobal(_overlappedPointer);
            _wait.Dispose();
            if (_pinned is { IsAllocated: true } pinned)
            {
                pinned.Free();
            }
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort WReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public ushort WReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedNative
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
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
    private static extern bool GetCommState(SafeFileHandle hFile, ref Dcb lpDCB);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle hFile, ref Dcb lpDCB);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommModemStatus(SafeFileHandle hFile, out uint lpModemStat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommMask(SafeFileHandle hFile, uint dwEvtMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitCommEvent(
        SafeFileHandle hFile,
        IntPtr lpEvtMask,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        int nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        int nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        IntPtr lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(
        SafeFileHandle hFile,
        IntPtr lpOverlapped);
}
