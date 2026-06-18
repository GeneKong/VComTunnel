using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VComTunnel.Core;

var options = SmokeOptions.Parse(args);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
if (options.ProbeOnly)
{
    Task? probeServer = null;
    if (options.ProbeFakeServer)
    {
        probeServer = FakeRfc2217EchoServer.RunAsync(options.Port, null, cts.Token);
        await Task.Delay(100, cts.Token);
    }

    try
    {
        await ProbeRfc2217EndpointAsync(options, cts.Token);
    }
    finally
    {
        cts.Cancel();
        if (probeServer is not null)
        {
            try
            {
                await probeServer;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    return;
}

Task? server = null;
FakeRfc2217Probe? probe = null;
if (!options.Remote)
{
    probe = new FakeRfc2217Probe();
    server = FakeRfc2217EchoServer.RunAsync(options.Port, probe, cts.Token);
}

var log = new InMemoryLog();
using var session = new KmdfTunnelSession(
    new TunnelMapping
    {
        Id = "smoke",
        Name = "KMDF Smoke",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = options.PortName,
        BackingPort = null,
        Host = options.Host,
        Port = options.Port,
        Protocol = TunnelProtocol.Rfc2217,
        RestartOnFailure = false
    },
    log,
    (_, error) => Console.WriteLine($"fault: {error}"));

await session.StartAsync(cts.Token);
await Task.Delay(300, cts.Token);

var payload = new byte[] { 0x56, 0x43, 0x54, 0xFF, 0x4F, 0x4B };
using var serial = NativeSerial.Open(options.PortName);
var controlTxBytes = 0;
if (options.ControlIoctls)
{
    controlTxBytes = await ExerciseControlIoctlsAsync(serial, probe, options.ExpectEcho, TimeSpan.FromSeconds(options.ReadSeconds), cts.Token);
}

try
{
    serial.Write(payload);
}
catch
{
    DumpLogs(log);
    throw;
}
await Task.Delay(300, cts.Token);
Console.WriteLine($"inQueue before read: {serial.GetInQueue()}");
var received = await serial.ReadUntilAsync(payload.Length, TimeSpan.FromSeconds(options.ReadSeconds), cts.Token);
Console.WriteLine($"inQueue after read: {serial.GetInQueue()}");

Console.WriteLine($"tx: {Convert.ToHexString(payload)}");
Console.WriteLine($"rx: {Convert.ToHexString(received)}");
var entries = log.Snapshot(20);
DumpLogEntries(entries);

var hasTx = entries.Any(entry => entry.Message.Contains("KMDF TX event", StringComparison.OrdinalIgnoreCase));
if (!hasTx)
{
    throw new InvalidOperationException("KMDF did not report a TX event.");
}

if (options.ExpectEcho && !payload.SequenceEqual(received))
{
    throw new InvalidOperationException("RFC2217 smoke echo mismatch.");
}

if (options.ControlIoctls)
{
    var stats = serial.GetStats();
    var expectedTx = (uint)(payload.Length + controlTxBytes);
    if (stats.TransmittedCount < expectedTx)
    {
        throw new InvalidOperationException($"Expected at least {expectedTx} transmitted byte(s), stats reported {stats.TransmittedCount}.");
    }

    if (options.ExpectEcho && stats.ReceivedCount < expectedTx)
    {
        throw new InvalidOperationException($"Expected at least {expectedTx} received byte(s), stats reported {stats.ReceivedCount}.");
    }

    Console.WriteLine($"stats: rx={stats.ReceivedCount} tx={stats.TransmittedCount}");
}

session.Dispose();
cts.Cancel();
if (server is not null)
{
    try
    {
        await server;
    }
    catch (OperationCanceledException)
    {
    }
    catch (IOException)
    {
    }
}

if (payload.SequenceEqual(received))
{
    Console.WriteLine("RFC2217 smoke passed.");
}
else if (options.Remote)
{
    Console.WriteLine("RFC2217 remote smoke completed. TX path was observed; RX echo was not required.");
}
else
{
    throw new InvalidOperationException("RFC2217 smoke echo mismatch.");
}

static async Task ProbeRfc2217EndpointAsync(SmokeOptions options, CancellationToken cancellationToken)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(options.Host, options.Port, cancellationToken);
    using var stream = tcp.GetStream();

    var client = new Rfc2217Client();
    var initial = await ProbeRfc2217ExchangeAsync(
        stream,
        client,
        "initial negotiation",
        client.BuildInitialNegotiation(),
        Rfc2217Client.BuildInitialExpectedAcks(),
        options.ReadSeconds,
        cancellationToken,
        allowMissingAcks: true);
    Console.WriteLine($"RFC2217 probe {options.Host}:{options.Port}");
    DumpProbeExchange(initial);
    var degraded = initial.MissingAcks.Count > 0;

    if (options.ProbeQuery)
    {
        var serialQuery = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "current serial settings",
            Rfc2217Client.BuildQuerySerialSettings(),
            [
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetBaudRate, [0, 0, 0, 0], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetDataSize, [0], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetParity, [0], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetStopSize, [0], AllowAcceptedValue: true)
            ],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(serialQuery);

        var controlQuery = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "current control state",
            Rfc2217Client.BuildQueryControlState(),
            [
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [0], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [4], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [7], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [10], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [13], AllowAcceptedValue: true)
            ],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(controlQuery);
    }

    if (options.ProbeSettings)
    {
        var baud = 115200u;
        var baudExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "baud-rate 115200",
            Rfc2217Client.BuildSetBaudRate(baud),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckSetBaudRate, Rfc2217Client.BuildUInt32Payload(baud), AllowAcceptedValue: true)],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(baudExchange);

        var lineExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "line-control 8N1",
            Rfc2217Client.BuildSetLineControl(stopBits: 0, parity: 0, wordLength: 8),
            [
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetDataSize, [8], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetParity, [1], AllowAcceptedValue: true),
                new Rfc2217ExpectedAck(Rfc2217Client.AckSetStopSize, [1], AllowAcceptedValue: true)
            ],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(lineExchange);
    }

    if (options.ProbeControls)
    {
        var dtrExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "DTR on",
            Rfc2217Client.BuildSetModemControl(dtr: true, rts: null),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [8])],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(dtrExchange);

        var rtsExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "RTS on",
            Rfc2217Client.BuildSetModemControl(dtr: null, rts: true),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [11])],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(rtsExchange);

        var breakOnExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "BREAK on",
            Rfc2217Client.BuildSetBreak(enabled: true),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [5])],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(breakOnExchange);

        var breakOffExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "BREAK off",
            Rfc2217Client.BuildSetBreak(enabled: false),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [6])],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(breakOffExchange);

        var purgeExchange = await ProbeRfc2217ExchangeAsync(
            stream,
            client,
            "purge rx+tx",
            Rfc2217Client.BuildPurge(NativeSerial.PurgeTxClear | NativeSerial.PurgeRxClear),
            [new Rfc2217ExpectedAck(Rfc2217Client.AckPurgeData, [3])],
            options.ReadSeconds,
            cancellationToken);
        DumpProbeExchange(purgeExchange);
    }

    Console.WriteLine(degraded
        ? "RFC2217 probe completed with degraded initial status-mask support."
        : "RFC2217 probe passed.");
}

static async Task<Rfc2217ProbeExchange> ProbeRfc2217ExchangeAsync(
    NetworkStream stream,
    Rfc2217Client client,
    string description,
    byte[] request,
    IReadOnlyList<Rfc2217ExpectedAck> expectedAcks,
    int seconds,
    CancellationToken cancellationToken,
    bool allowMissingAcks = false)
{
    var pendingAcks = expectedAcks.ToList();
    var notifications = new List<Rfc2217Notification>();
    var buffer = new byte[4096];
    var receivedBytes = 0;
    var replyBytes = 0;
    var serialBytes = 0;
    var telnetOptions = new List<Rfc2217TelnetOptionEvent>();

    await stream.WriteAsync(request, cancellationToken);
    await stream.FlushAsync(cancellationToken);

    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
    while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            break;
        }

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));

        int read;
        try
        {
            read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            continue;
        }

        if (read == 0)
        {
            break;
        }

        receivedBytes += read;
        var frame = client.ProcessNetworkBytes(buffer, read);
        serialBytes += frame.SerialData.Length;
        telnetOptions.AddRange(frame.TelnetOptions);

        if (frame.Replies.Length > 0)
        {
            await stream.WriteAsync(frame.Replies, cancellationToken);
            replyBytes += frame.Replies.Length;
        }

        foreach (var notification in frame.Notifications)
        {
            notifications.Add(notification);
            var index = pendingAcks.FindIndex(expected => ProbeAckMatches(expected, notification));
            if (index >= 0)
            {
                pendingAcks.RemoveAt(index);
            }
        }

        if (pendingAcks.Count == 0)
        {
            break;
        }
    }

    if (pendingAcks.Count != 0)
    {
        var missing = string.Join(", ", pendingAcks.Select(ack => ack.Describe()));
        if (!allowMissingAcks)
        {
            var observed = notifications.Count == 0
                ? "none"
                : string.Join(", ", notifications.Select(Rfc2217ExpectedAck.Describe));
            throw new InvalidOperationException($"RFC2217 probe did not observe {description} ACK(s): {missing}. Observed: {observed}; serial bytes: {serialBytes}; telnet replies: {replyBytes}; received bytes: {receivedBytes}.");
        }
    }

    return new Rfc2217ProbeExchange(description, request.Length, receivedBytes, replyBytes, serialBytes, notifications, pendingAcks, telnetOptions);
}

static bool ProbeAckMatches(Rfc2217ExpectedAck expected, Rfc2217Notification notification)
{
    return expected.Matches(notification)
        || expected.MatchesAcceptedValue(notification)
        || expected.MatchesAcceptedSetControlValue(notification);
}

static void DumpProbeExchange(Rfc2217ProbeExchange exchange)
{
    Console.WriteLine($"exchange: {exchange.Description}");
    Console.WriteLine($"  sent: {exchange.SentBytes} byte(s)");
    Console.WriteLine($"  received: {exchange.ReceivedBytes} byte(s), telnet replies: {exchange.TelnetReplyBytes} byte(s), serial data: {exchange.SerialBytes} byte(s)");
    Console.WriteLine("  notifications:");
    if (exchange.Notifications.Count == 0)
    {
        Console.WriteLine("    none");
    }
    else
    {
        foreach (var notification in exchange.Notifications)
        {
            Console.WriteLine($"    {Rfc2217ExpectedAck.Describe(notification)}");
        }
    }
    if (exchange.MissingAcks.Count > 0)
    {
        Console.WriteLine("  missing ACKs:");
        foreach (var ack in exchange.MissingAcks)
        {
            Console.WriteLine($"    {ack.Describe()}");
        }
    }
    if (exchange.TelnetOptions.Count > 0)
    {
        Console.WriteLine("  telnet options:");
        foreach (var option in exchange.TelnetOptions)
        {
            Console.WriteLine($"    {option.Describe()}");
        }
    }
}

static void DumpLogs(InMemoryLog log) => DumpLogEntries(log.Snapshot(20));

static async Task<int> ExerciseControlIoctlsAsync(
    NativeSerial serial,
    FakeRfc2217Probe? probe,
    bool expectEcho,
    TimeSpan readTimeout,
    CancellationToken cancellationToken)
{
    const byte setControl = 5;
    const byte setBaudRate = 1;
    const byte setDataSize = 2;
    const byte setParity = 3;
    const byte setStopSize = 4;
    const byte purgeData = 12;
    const byte localFlowControlSuspend = 8;
    const byte localFlowControlResume = 9;

    serial.ClearStats();
    serial.ValidateCommConfig();
    serial.SetQueueSize(4096, 4096);
    await WaitForRemoteNotificationsAsync(serial, probe, readTimeout, cancellationToken);

    serial.SetBaudRate(115200);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setBaudRate && notification.Payload is [0x00, 0x01, 0xC2, 0x00],
        "SET-BAUDRATE 115200",
        readTimeout,
        cancellationToken);

    serial.SetLineControl(stopBits: 0, parity: 0, wordLength: 8);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setDataSize && notification.Payload is [8],
        "SET-DATASIZE 8",
        readTimeout,
        cancellationToken);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setParity && notification.Payload is [1],
        "SET-PARITY none",
        readTimeout,
        cancellationToken);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setStopSize && notification.Payload is [1],
        "SET-STOPSIZE 1",
        readTimeout,
        cancellationToken);

    serial.SetRawModemControl(NativeSerial.McrDtr | NativeSerial.McrRts | NativeSerial.McrLoop);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [8],
        "SET-CONTROL DTR on",
        readTimeout,
        cancellationToken);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [11],
        "SET-CONTROL RTS on",
        readTimeout,
        cancellationToken);

    var rawModemControl = serial.GetRawModemControl();
    var expectedModemControl = NativeSerial.McrDtr | NativeSerial.McrRts | NativeSerial.McrLoop;
    if ((rawModemControl & expectedModemControl) != expectedModemControl)
    {
        throw new InvalidOperationException($"Raw modem control mismatch: 0x{rawModemControl:X8}.");
    }

    serial.SetHandflow(controlHandshake: 0x20, flowReplace: 0x80);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [17],
        "SET-CONTROL outbound DCD flow",
        readTimeout,
        cancellationToken);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [16],
        "SET-CONTROL inbound RTS flow",
        readTimeout,
        cancellationToken);

    serial.SetBreak(enabled: true);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [5],
        "SET-CONTROL BREAK on",
        readTimeout,
        cancellationToken);
    serial.SetBreak(enabled: false);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == setControl && notification.Payload is [6],
        "SET-CONTROL BREAK off",
        readTimeout,
        cancellationToken);

    serial.Purge(NativeSerial.PurgeTxClear | NativeSerial.PurgeRxClear);
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == purgeData && notification.Payload is [3],
        "PURGE-DATA rx+tx",
        readTimeout,
        cancellationToken);

    serial.SetImmediateReadTimeout();
    var emptyRead = serial.ReadOnce(1);
    if (emptyRead.Length != 0)
    {
        throw new InvalidOperationException($"Expected immediate empty read after purge, got {Convert.ToHexString(emptyRead)}.");
    }
    Console.WriteLine("read timeout: immediate empty read returned 0 byte(s).");

    serial.SetXoff();
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == localFlowControlSuspend && notification.Payload.Length == 0,
        "FLOWCONTROL-SUSPEND",
        readTimeout,
        cancellationToken);

    serial.SetXon();
    await WaitForRfc2217Async(
        probe,
        notification => notification.Command == localFlowControlResume && notification.Payload.Length == 0,
        "FLOWCONTROL-RESUME",
        readTimeout,
        cancellationToken);

    if (!expectEcho)
    {
        Console.WriteLine("control ioctls: passed without immediate echo check.");
        return 0;
    }

    const byte immediate = 0x7E;
    serial.SendImmediate(immediate);
    var echoed = await serial.ReadUntilAsync(1, readTimeout, cancellationToken);
    if (echoed.Length != 1 || echoed[0] != immediate)
    {
        throw new InvalidOperationException($"Immediate char echo mismatch: {Convert.ToHexString(echoed)}.");
    }

    Console.WriteLine("control ioctls: passed.");
    return 1;
}

static Task WaitForRfc2217Async(
    FakeRfc2217Probe? probe,
    Func<Rfc2217Notification, bool> predicate,
    string description,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    return probe is null
        ? Task.CompletedTask
        : probe.WaitForAsync(predicate, description, timeout, cancellationToken);
}

static async Task WaitForRemoteNotificationsAsync(
    NativeSerial serial,
    FakeRfc2217Probe? probe,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    if (probe is null)
    {
        return;
    }

    var modemStatus = await serial.WaitForModemStatusAsync(NativeSerial.ExpectedRemoteModemStatus, timeout, cancellationToken);
    var lineErrors = await serial.WaitForCommErrorsAsync(NativeSerial.ExpectedRemoteLineErrors, timeout, cancellationToken);
    Console.WriteLine($"remote notifications: modem=0x{modemStatus:X8} errors=0x{lineErrors:X8}");
}

static void DumpLogEntries(IReadOnlyList<LogEntry> entries)
{
    foreach (var entry in entries)
    {
        Console.WriteLine($"{entry.Level} {entry.Source}: {entry.Message}");
    }
}

internal sealed record SmokeOptions(
    string PortName,
    string Host,
    int Port,
    bool Remote,
    bool ExpectEcho,
    bool ControlIoctls,
    bool ProbeOnly,
    bool ProbeQuery,
    bool ProbeSettings,
    bool ProbeControls,
    bool ProbeFakeServer,
    int TimeoutSeconds,
    int ReadSeconds)
{
    public static SmokeOptions Parse(string[] args)
    {
        var probeOnly = args.Any(arg => string.Equals(arg, "--probe-rfc2217", StringComparison.OrdinalIgnoreCase));
        var probeQuery = args.Any(arg => string.Equals(arg, "--probe-query", StringComparison.OrdinalIgnoreCase));
        var probeSettings = args.Any(arg => string.Equals(arg, "--probe-settings", StringComparison.OrdinalIgnoreCase));
        var probeControls = args.Any(arg => string.Equals(arg, "--probe-controls", StringComparison.OrdinalIgnoreCase));
        var probeFakeServer = args.Any(arg => string.Equals(arg, "--fake-server", StringComparison.OrdinalIgnoreCase));
        var remote = args.Any(arg => string.Equals(arg, "--remote", StringComparison.OrdinalIgnoreCase));
        var expectEcho = args.Any(arg => string.Equals(arg, "--expect-echo", StringComparison.OrdinalIgnoreCase)) || !remote;
        var skipControlIoctls = args.Any(arg => string.Equals(arg, "--no-control-ioctls", StringComparison.OrdinalIgnoreCase));
        var controlIoctls = args.Any(arg => string.Equals(arg, "--control-ioctls", StringComparison.OrdinalIgnoreCase)) ||
            (!remote && !skipControlIoctls);
        var values = args
            .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (probeOnly)
        {
            if (values.Length < 2 || !int.TryParse(values[1], out var probePort) || probePort <= 0)
            {
                throw new ArgumentException("Usage: VComTunnel.Smoke --probe-rfc2217 10.0.2.196 5000 [seconds] [--probe-query] [--probe-settings] [--probe-controls] [--fake-server]");
            }

            var seconds = values.Length >= 3 && int.TryParse(values[2], out var parsedSeconds) && parsedSeconds > 0
                ? parsedSeconds
                : 5;
            var exchangeCount = 1 + (probeQuery ? 2 : 0) + (probeSettings ? 2 : 0) + (probeControls ? 5 : 0);
            return new SmokeOptions("", values[0], probePort, true, false, false, true, probeQuery, probeSettings, probeControls, probeFakeServer, (seconds * exchangeCount) + 3, seconds);
        }

        if (remote)
        {
            if (values.Length < 3 || !int.TryParse(values[2], out var remotePort) || remotePort <= 0)
            {
                throw new ArgumentException("Usage: VComTunnel.Smoke --remote COM27 10.0.2.196 5000 [--expect-echo] [--control-ioctls]");
            }

            return new SmokeOptions(values[0], values[1], remotePort, true, expectEcho, controlIoctls, false, false, false, false, false, 10, 2);
        }

        var portName = values.FirstOrDefault() ?? "COM27";
        var tcpPort = values.Skip(1).Select(value => int.TryParse(value, out var parsed) ? parsed : 0).FirstOrDefault();
        if (tcpPort <= 0)
        {
            tcpPort = 44000;
        }

        return new SmokeOptions(portName, "127.0.0.1", tcpPort, false, expectEcho, controlIoctls, false, false, false, false, false, 10, 3);
    }
}

internal sealed record Rfc2217ProbeExchange(
    string Description,
    int SentBytes,
    int ReceivedBytes,
    int TelnetReplyBytes,
    int SerialBytes,
    IReadOnlyList<Rfc2217Notification> Notifications,
    IReadOnlyList<Rfc2217ExpectedAck> MissingAcks,
    IReadOnlyList<Rfc2217TelnetOptionEvent> TelnetOptions);

internal sealed record SerialStats(
    uint ReceivedCount,
    uint TransmittedCount,
    uint FrameErrorCount,
    uint SerialOverrunErrorCount,
    uint BufferOverrunErrorCount,
    uint ParityErrorCount);

internal sealed class NativeSerial : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    public const uint McrDtr = 0x00000001;
    public const uint McrRts = 0x00000002;
    public const uint McrLoop = 0x00000010;
    public const uint PurgeTxClear = 0x00000004;
    public const uint PurgeRxClear = 0x00000008;
    public const uint ExpectedRemoteModemStatus = 0x000000F0;
    public const uint ExpectedRemoteLineErrors = 0x00000017;

    private readonly SafeFileHandle _handle;

    private NativeSerial(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static NativeSerial Open(string portName)
    {
        var path = portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? portName : $@"\\.\{portName}";
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {path}.");
        }

        return new NativeSerial(handle);
    }

    public void Write(byte[] data)
    {
        if (!WriteFile(_handle, data, (uint)data.Length, out var written, IntPtr.Zero) ||
            written != data.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile wrote {written}/{data.Length} byte(s).");
        }
    }

    public async Task<byte[]> ReadUntilAsync(int byteCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var received = new List<byte>(byteCount);
        var buffer = new byte[byteCount];

        while (DateTimeOffset.UtcNow < deadline && received.Count < byteCount)
        {
            var available = GetInQueue();
            if (available == 0)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            var requested = Math.Min((uint)(byteCount - received.Count), available);
            if (!ReadFile(_handle, buffer, requested, out var read, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed.");
            }

            for (var i = 0; i < read; i++)
            {
                received.Add(buffer[i]);
            }

            if (read == 0)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        return received.ToArray();
    }

    public uint GetInQueue()
    {
        return BitConverter.ToUInt32(GetCommStatus(), 8);
    }

    public uint GetCommErrors()
    {
        return BitConverter.ToUInt32(GetCommStatus(), 0);
    }

    public uint GetModemStatus()
    {
        var output = new byte[4];
        DeviceIoControlChecked(IoctlSerialGetModemStatus, null, output, "IOCTL_SERIAL_GET_MODEMSTATUS");
        return BitConverter.ToUInt32(output);
    }

    public async Task<uint> WaitForModemStatusAsync(uint expectedMask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = GetModemStatus();
            if ((status & expectedMask) == expectedMask)
            {
                return status;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new InvalidOperationException($"Timed out waiting for modem status 0x{expectedMask:X8}; last value was 0x{GetModemStatus():X8}.");
    }

    public async Task<uint> WaitForCommErrorsAsync(uint expectedMask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var errors = GetCommErrors();
            if ((errors & expectedMask) == expectedMask)
            {
                return errors;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new InvalidOperationException($"Timed out waiting for comm errors 0x{expectedMask:X8}; last value was 0x{GetCommErrors():X8}.");
    }

    public void ValidateCommConfig()
    {
        var sizeBuffer = new byte[4];
        DeviceIoControlChecked(IoctlSerialConfigSize, null, sizeBuffer, "IOCTL_SERIAL_CONFIG_SIZE");
        var size = BitConverter.ToUInt32(sizeBuffer);
        if (size < 16 || size > 4096)
        {
            throw new InvalidOperationException($"Unexpected SERIALCONFIG size {size}.");
        }

        var config = new byte[size];
        var returned = DeviceIoControlChecked(IoctlSerialGetCommConfig, null, config, "IOCTL_SERIAL_GET_COMMCONFIG");
        if (returned != size || BitConverter.ToUInt32(config) != size)
        {
            throw new InvalidOperationException($"Invalid SERIALCONFIG response bytes={returned} size={BitConverter.ToUInt32(config)}.");
        }

        DeviceIoControlChecked(IoctlSerialSetCommConfig, config, null, "IOCTL_SERIAL_SET_COMMCONFIG");
    }

    public void SetQueueSize(uint inSize, uint outSize)
    {
        var input = new byte[8];
        BitConverter.GetBytes(inSize).CopyTo(input, 0);
        BitConverter.GetBytes(outSize).CopyTo(input, 4);
        DeviceIoControlChecked(IoctlSerialSetQueueSize, input, null, "IOCTL_SERIAL_SET_QUEUE_SIZE");
    }

    public void SetBaudRate(uint baudRate)
    {
        DeviceIoControlChecked(IoctlSerialSetBaudRate, BitConverter.GetBytes(baudRate), null, "IOCTL_SERIAL_SET_BAUD_RATE");
    }

    public void SetLineControl(byte stopBits, byte parity, byte wordLength)
    {
        DeviceIoControlChecked(IoctlSerialSetLineControl, [stopBits, parity, wordLength], null, "IOCTL_SERIAL_SET_LINE_CONTROL");
    }

    public void SetHandflow(uint controlHandshake, uint flowReplace)
    {
        var input = new byte[16];
        BitConverter.GetBytes(controlHandshake).CopyTo(input, 0);
        BitConverter.GetBytes(flowReplace).CopyTo(input, 4);
        DeviceIoControlChecked(IoctlSerialSetHandflow, input, null, "IOCTL_SERIAL_SET_HANDFLOW");
    }

    public void ClearStats()
    {
        DeviceIoControlChecked(IoctlSerialClearStats, null, null, "IOCTL_SERIAL_CLEAR_STATS");
    }

    public SerialStats GetStats()
    {
        var output = new byte[24];
        DeviceIoControlChecked(IoctlSerialGetStats, null, output, "IOCTL_SERIAL_GET_STATS");
        return new SerialStats(
            BitConverter.ToUInt32(output, 0),
            BitConverter.ToUInt32(output, 4),
            BitConverter.ToUInt32(output, 8),
            BitConverter.ToUInt32(output, 12),
            BitConverter.ToUInt32(output, 16),
            BitConverter.ToUInt32(output, 20));
    }

    public uint GetRawModemControl()
    {
        var output = new byte[4];
        DeviceIoControlChecked(IoctlSerialGetModemControl, null, output, "IOCTL_SERIAL_GET_MODEM_CONTROL");
        return BitConverter.ToUInt32(output);
    }

    public void SetRawModemControl(uint modemControl)
    {
        DeviceIoControlChecked(IoctlSerialSetModemControl, BitConverter.GetBytes(modemControl), null, "IOCTL_SERIAL_SET_MODEM_CONTROL");
    }

    public void SetXoff()
    {
        DeviceIoControlChecked(IoctlSerialSetXoff, null, null, "IOCTL_SERIAL_SET_XOFF");
    }

    public void SetXon()
    {
        DeviceIoControlChecked(IoctlSerialSetXon, null, null, "IOCTL_SERIAL_SET_XON");
    }

    public void SetBreak(bool enabled)
    {
        DeviceIoControlChecked(enabled ? IoctlSerialSetBreakOn : IoctlSerialSetBreakOff, null, null, enabled ? "IOCTL_SERIAL_SET_BREAK_ON" : "IOCTL_SERIAL_SET_BREAK_OFF");
    }

    public void Purge(uint purgeMask)
    {
        DeviceIoControlChecked(IoctlSerialPurge, BitConverter.GetBytes(purgeMask), null, "IOCTL_SERIAL_PURGE");
    }

    public void SetImmediateReadTimeout()
    {
        var input = new byte[20];
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(input, 0);
        DeviceIoControlChecked(IoctlSerialSetTimeouts, input, null, "IOCTL_SERIAL_SET_TIMEOUTS");
    }

    public byte[] ReadOnce(int byteCount)
    {
        var buffer = new byte[byteCount];
        if (!ReadFile(_handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed.");
        }

        return buffer.Take((int)read).ToArray();
    }

    public void SendImmediate(byte value)
    {
        DeviceIoControlChecked(IoctlSerialImmediateChar, [value], null, "IOCTL_SERIAL_IMMEDIATE_CHAR");
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        uint bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        IntPtr overlapped);

    private const uint IoctlSerialSetBaudRate = (0x1Bu << 16) | (1u << 2);
    private const uint IoctlSerialSetQueueSize = (0x1Bu << 16) | (2u << 2);
    private const uint IoctlSerialSetLineControl = (0x1Bu << 16) | (3u << 2);
    private const uint IoctlSerialSetBreakOn = (0x1Bu << 16) | (4u << 2);
    private const uint IoctlSerialSetBreakOff = (0x1Bu << 16) | (5u << 2);
    private const uint IoctlSerialImmediateChar = (0x1Bu << 16) | (6u << 2);
    private const uint IoctlSerialSetTimeouts = (0x1Bu << 16) | (7u << 2);
    private const uint IoctlSerialSetXoff = (0x1Bu << 16) | (14u << 2);
    private const uint IoctlSerialSetXon = (0x1Bu << 16) | (15u << 2);
    private const uint IoctlSerialPurge = (0x1Bu << 16) | (19u << 2);
    private const uint IoctlSerialSetHandflow = (0x1Bu << 16) | (25u << 2);
    private const uint IoctlSerialGetModemStatus = (0x1Bu << 16) | (26u << 2);
    private const uint IoctlSerialGetCommStatus = (0x1Bu << 16) | (27u << 2);
    private const uint IoctlSerialConfigSize = (0x1Bu << 16) | (32u << 2);
    private const uint IoctlSerialGetCommConfig = (0x1Bu << 16) | (33u << 2);
    private const uint IoctlSerialSetCommConfig = (0x1Bu << 16) | (34u << 2);
    private const uint IoctlSerialGetStats = (0x1Bu << 16) | (35u << 2);
    private const uint IoctlSerialClearStats = (0x1Bu << 16) | (36u << 2);
    private const uint IoctlSerialGetModemControl = (0x1Bu << 16) | (37u << 2);
    private const uint IoctlSerialSetModemControl = (0x1Bu << 16) | (38u << 2);

    private int DeviceIoControlChecked(uint controlCode, byte[]? input, byte[]? output, string description)
    {
        if (!DeviceIoControl(
            _handle,
            controlCode,
            input,
            input?.Length ?? 0,
            output,
            output?.Length ?? 0,
            out var bytesReturned,
            IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{description} failed.");
        }

        return bytesReturned;
    }

    private byte[] GetCommStatus()
    {
        var output = new byte[24];
        DeviceIoControlChecked(IoctlSerialGetCommStatus, null, output, "IOCTL_SERIAL_GET_COMMSTATUS");
        return output;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle file,
        uint controlCode,
        byte[]? inBuffer,
        int inBufferSize,
        byte[]? outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

internal sealed class FakeRfc2217Probe
{
    private readonly object _gate = new();
    private readonly List<Rfc2217Notification> _notifications = [];

    public void Record(Rfc2217Notification notification)
    {
        lock (_gate)
        {
            _notifications.Add(new Rfc2217Notification(notification.Command, notification.Payload.ToArray()));
        }
    }

    public async Task WaitForAsync(
        Func<Rfc2217Notification, bool> predicate,
        string description,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_notifications.Any(predicate))
                {
                    return;
                }
            }

            await Task.Delay(20, cancellationToken);
        }

        var seen = Snapshot();
        var details = seen.Length == 0
            ? "none"
            : string.Join(", ", seen.Select(Rfc2217ExpectedAck.Describe));
        throw new InvalidOperationException($"Timed out waiting for RFC2217 {description}. Seen: {details}.");
    }

    private Rfc2217Notification[] Snapshot()
    {
        lock (_gate)
        {
            return _notifications
                .Select(notification => new Rfc2217Notification(notification.Command, notification.Payload.ToArray()))
                .ToArray();
        }
    }
}

internal static class FakeRfc2217EchoServer
{
    public static async Task RunAsync(int port, FakeRfc2217Probe? probe, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var negotiation = new byte[] { 255, 251, 44, 255, 253, 0, 255, 251, 0 };
            await stream.WriteAsync(negotiation, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var parser = new Rfc2217Client();
            var buffer = new byte[4096];
            var sentStatusNotifications = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                var frame = parser.ProcessNetworkBytes(buffer, read);
                if (frame.Replies.Length > 0)
                {
                    await stream.WriteAsync(frame.Replies, cancellationToken);
                }

                foreach (var notification in frame.Notifications)
                {
                    probe?.Record(notification);
                    var ack = BuildServerAck(notification);
                    if (ack.Length > 0)
                    {
                        await stream.WriteAsync(ack, cancellationToken);
                    }
                }

                if (!sentStatusNotifications)
                {
                    sentStatusNotifications = true;
                    await stream.WriteAsync(BuildServerFrame(Rfc2217Client.NotifyModemState, [0xF0]), cancellationToken);
                    await stream.WriteAsync(BuildServerFrame(Rfc2217Client.NotifyLineState, [0x1E]), cancellationToken);
                }

                if (frame.SerialData.Length > 0)
                {
                    var echo = Rfc2217Client.EscapeSerialData(frame.SerialData, 0, frame.SerialData.Length);
                    await stream.WriteAsync(echo, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] BuildServerAck(Rfc2217Notification notification)
    {
        var payload = BuildServerAckPayload(notification);
        var command = notification.Command switch
        {
            1 => Rfc2217Client.AckSetBaudRate,
            2 => Rfc2217Client.AckSetDataSize,
            3 => Rfc2217Client.AckSetParity,
            4 => Rfc2217Client.AckSetStopSize,
            5 => Rfc2217Client.AckSetControl,
            10 => Rfc2217Client.AckSetLineStateMask,
            11 => Rfc2217Client.AckSetModemStateMask,
            12 => Rfc2217Client.AckPurgeData,
            _ => (byte)0
        };

        if (command == 0)
        {
            return [];
        }

        return BuildServerFrame(command, payload);
    }

    private static byte[] BuildServerAckPayload(Rfc2217Notification notification)
    {
        if (notification.Payload.Length == 0)
        {
            return notification.Payload;
        }

        return notification.Command switch
        {
            1 when notification.Payload is [0, 0, 0, 0] => [0x00, 0x01, 0xC2, 0x00],
            2 when notification.Payload is [0] => [8],
            3 when notification.Payload is [0] => [1],
            4 when notification.Payload is [0] => [1],
            5 when notification.Payload is [0] => [1],
            5 when notification.Payload is [4] => [6],
            5 when notification.Payload is [7] => [8],
            5 when notification.Payload is [10] => [11],
            5 when notification.Payload is [13] => [14],
            _ => notification.Payload
        };
    }

    private static byte[] BuildServerFrame(byte command, params byte[] payload)
    {
        var frame = new List<byte>(payload.Length + 5)
        {
            255,
            250,
            44,
            command
        };
        foreach (var value in payload)
        {
            frame.Add(value);
            if (value == 255)
            {
                frame.Add(255);
            }
        }

        frame.Add(255);
        frame.Add(240);
        return frame.ToArray();
    }
}
