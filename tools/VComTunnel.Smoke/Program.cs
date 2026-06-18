using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VComTunnel.Core;

var options = SmokeOptions.Parse(args);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
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

static void DumpLogs(InMemoryLog log) => DumpLogEntries(log.Snapshot(20));

static async Task<int> ExerciseControlIoctlsAsync(
    NativeSerial serial,
    FakeRfc2217Probe? probe,
    bool expectEcho,
    TimeSpan readTimeout,
    CancellationToken cancellationToken)
{
    const byte setControl = 5;
    const byte localFlowControlSuspend = 8;
    const byte localFlowControlResume = 9;

    serial.ClearStats();
    serial.ValidateCommConfig();
    serial.SetQueueSize(4096, 4096);

    serial.SetRawModemControl(NativeSerial.McrDtr | NativeSerial.McrRts | NativeSerial.McrLoop);
    if (probe is not null)
    {
        await probe.WaitForAsync(
            notification => notification.Command == setControl && notification.Payload is [8],
            "SET-CONTROL DTR on",
            readTimeout,
            cancellationToken);
        await probe.WaitForAsync(
            notification => notification.Command == setControl && notification.Payload is [11],
            "SET-CONTROL RTS on",
            readTimeout,
            cancellationToken);
    }

    var rawModemControl = serial.GetRawModemControl();
    var expectedModemControl = NativeSerial.McrDtr | NativeSerial.McrRts | NativeSerial.McrLoop;
    if ((rawModemControl & expectedModemControl) != expectedModemControl)
    {
        throw new InvalidOperationException($"Raw modem control mismatch: 0x{rawModemControl:X8}.");
    }

    serial.SetXoff();
    if (probe is not null)
    {
        await probe.WaitForAsync(
            notification => notification.Command == localFlowControlSuspend && notification.Payload.Length == 0,
            "FLOWCONTROL-SUSPEND",
            readTimeout,
            cancellationToken);
    }

    serial.SetXon();
    if (probe is not null)
    {
        await probe.WaitForAsync(
            notification => notification.Command == localFlowControlResume && notification.Payload.Length == 0,
            "FLOWCONTROL-RESUME",
            readTimeout,
            cancellationToken);
    }

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
    int TimeoutSeconds,
    int ReadSeconds)
{
    public static SmokeOptions Parse(string[] args)
    {
        var remote = args.Any(arg => string.Equals(arg, "--remote", StringComparison.OrdinalIgnoreCase));
        var expectEcho = args.Any(arg => string.Equals(arg, "--expect-echo", StringComparison.OrdinalIgnoreCase)) || !remote;
        var skipControlIoctls = args.Any(arg => string.Equals(arg, "--no-control-ioctls", StringComparison.OrdinalIgnoreCase));
        var controlIoctls = args.Any(arg => string.Equals(arg, "--control-ioctls", StringComparison.OrdinalIgnoreCase)) ||
            (!remote && !skipControlIoctls);
        var values = args
            .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (remote)
        {
            if (values.Length < 3 || !int.TryParse(values[2], out var remotePort) || remotePort <= 0)
            {
                throw new ArgumentException("Usage: VComTunnel.Smoke --remote COM27 10.0.2.196 5000 [--expect-echo] [--control-ioctls]");
            }

            return new SmokeOptions(values[0], values[1], remotePort, true, expectEcho, controlIoctls, 10, 2);
        }

        var portName = values.FirstOrDefault() ?? "COM27";
        var tcpPort = values.Skip(1).Select(value => int.TryParse(value, out var parsed) ? parsed : 0).FirstOrDefault();
        if (tcpPort <= 0)
        {
            tcpPort = 44000;
        }

        return new SmokeOptions(portName, "127.0.0.1", tcpPort, false, expectEcho, controlIoctls, 10, 3);
    }
}

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
            if (!ReadFile(_handle, buffer, (uint)(byteCount - received.Count), out var read, IntPtr.Zero))
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
        var output = new byte[24];
        DeviceIoControlChecked(IoctlSerialGetCommStatus, null, output, "IOCTL_SERIAL_GET_COMMSTATUS");

        return BitConverter.ToUInt32(output, 8);
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

    private const uint IoctlSerialSetQueueSize = (0x1Bu << 16) | (2u << 2);
    private const uint IoctlSerialImmediateChar = (0x1Bu << 16) | (6u << 2);
    private const uint IoctlSerialSetXoff = (0x1Bu << 16) | (14u << 2);
    private const uint IoctlSerialSetXon = (0x1Bu << 16) | (15u << 2);
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

        var frame = new List<byte>(notification.Payload.Length + 5)
        {
            255,
            250,
            44,
            command
        };
        foreach (var value in notification.Payload)
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
