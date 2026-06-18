using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VComTunnel.Core;

public sealed class KmdfTunnelSession : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int EventHeaderSize = 24;
    private const ushort EventTypeTxData = 1;
    private const int MaxEventBytes = 4096;
    private const int MaxRxBytes = 4096;

    private static readonly uint IoctlAttach = CtlCode(0x801);
    private static readonly uint IoctlWaitEvent = CtlCode(0x802);
    private static readonly uint IoctlPushRx = CtlCode(0x804);
    private static readonly uint IoctlSetConnectionState = CtlCode(0x805);
    private static readonly uint IoctlDetach = CtlCode(0x806);

    private readonly TunnelMapping _mapping;
    private readonly InMemoryLog _log;
    private readonly Action<KmdfTunnelSession, string> _faulted;
    private readonly MinimalRfc2217Filter _rfc2217 = new();
    private readonly CancellationTokenSource _stop = new();
    private SafeFileHandle? _eventDriver;
    private SafeFileHandle? _commandDriver;
    private TcpClient? _tcp;
    private Task? _eventLoop;
    private Task? _networkLoop;
    private int _disposed;

    public KmdfTunnelSession(TunnelMapping mapping, InMemoryLog log, Action<KmdfTunnelSession, string> faulted)
    {
        _mapping = mapping;
        _log = log;
        _faulted = faulted;
        State = TunnelRunState.Starting;
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

        SetConnectionState(2);
        State = TunnelRunState.Running;
        _log.Info(_mapping.Name, $"Started KMDF tunnel {_mapping.VisiblePort} -> {_mapping.Host}:{_mapping.Port}.");

        _eventLoop = Task.Run(EventLoopAsync);
        _networkLoop = Task.Run(NetworkLoopAsync);
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
        WriteUInt16(input, 0, 1);
        WriteUInt16(input, 2, 0);
        var instance = Encoding.Unicode.GetBytes(Environment.MachineName);
        Array.Copy(instance, 0, input, 8, Math.Min(instance.Length, 126));

        var output = new byte[80];
        DeviceIoControlChecked(_eventDriver, IoctlAttach, input, output);
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
                if (type != EventTypeTxData || size < EventHeaderSize || size > bytes)
                {
                    throw new InvalidOperationException($"KMDF driver returned unsupported event type {type}.");
                }

                var payloadBytes = checked((int)size - EventHeaderSize);
                var escaped = MinimalRfc2217Filter.EscapeSerialData(output, EventHeaderSize, payloadBytes);
                _log.Info(_mapping.Name, $"KMDF TX event {payloadBytes} byte(s).");
                await stream.WriteAsync(escaped, _stop.Token).ConfigureAwait(false);
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

                var frame = _rfc2217.ProcessNetworkBytes(buffer, read);
                if (frame.Replies.Length > 0)
                {
                    await stream.WriteAsync(frame.Replies, _stop.Token).ConfigureAwait(false);
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
                DeviceIoControlChecked(_commandDriver, IoctlPushRx, push, null);
                _log.Info(_mapping.Name, $"Driver RX push completed {frame.SerialData.Length} byte(s).");
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

    private sealed class MinimalRfc2217Filter
    {
        private const byte Iac = 255;
        private const byte Dont = 254;
        private const byte Do = 253;
        private const byte Wont = 252;
        private const byte Will = 251;
        private const byte Sb = 250;
        private const byte Se = 240;
        private const byte TelnetBinary = 0;
        private const byte SuppressGoAhead = 3;
        private const byte ComPortOption = 44;

        private ParserState _state;
        private byte _command;

        public Rfc2217Frame ProcessNetworkBytes(byte[] buffer, int length)
        {
            var serial = new List<byte>(length);
            var replies = new List<byte>();

            for (var i = 0; i < length; i++)
            {
                var value = buffer[i];
                switch (_state)
                {
                    case ParserState.Data:
                        if (value == Iac)
                        {
                            _state = ParserState.Iac;
                        }
                        else
                        {
                            serial.Add(value);
                        }
                        break;

                    case ParserState.Iac:
                        if (value == Iac)
                        {
                            serial.Add(Iac);
                            _state = ParserState.Data;
                        }
                        else if (value is Do or Dont or Will or Wont)
                        {
                            _command = value;
                            _state = ParserState.Option;
                        }
                        else if (value == Sb)
                        {
                            _state = ParserState.Subnegotiation;
                        }
                        else
                        {
                            _state = ParserState.Data;
                        }
                        break;

                    case ParserState.Option:
                        AddNegotiationReply(replies, _command, value);
                        _state = ParserState.Data;
                        break;

                    case ParserState.Subnegotiation:
                        if (value == Iac)
                        {
                            _state = ParserState.SubnegotiationIac;
                        }
                        break;

                    case ParserState.SubnegotiationIac:
                        _state = value == Se ? ParserState.Data : ParserState.Subnegotiation;
                        break;
                }
            }

            return new Rfc2217Frame(serial.ToArray(), replies.ToArray());
        }

        public static byte[] EscapeSerialData(byte[] buffer, int offset, int length)
        {
            var escaped = new List<byte>(length);
            for (var i = 0; i < length; i++)
            {
                var value = buffer[offset + i];
                escaped.Add(value);
                if (value == Iac)
                {
                    escaped.Add(Iac);
                }
            }

            return escaped.ToArray();
        }

        private static void AddNegotiationReply(List<byte> replies, byte command, byte option)
        {
            var accept = option is TelnetBinary or SuppressGoAhead or ComPortOption;
            var reply = command switch
            {
                Do => accept ? Will : Wont,
                Will => accept ? Do : Dont,
                _ => (byte)0
            };

            if (reply != 0)
            {
                replies.Add(Iac);
                replies.Add(reply);
                replies.Add(option);
            }
        }

        private enum ParserState
        {
            Data,
            Iac,
            Option,
            Subnegotiation,
            SubnegotiationIac
        }
    }

    private sealed record Rfc2217Frame(byte[] SerialData, byte[] Replies);

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
