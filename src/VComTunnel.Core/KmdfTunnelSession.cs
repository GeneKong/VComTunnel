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
    private const ushort EventTypeSetBaudRate = 2;
    private const ushort EventTypeSetLineControl = 3;
    private const ushort EventTypeSetModemControl = 4;
    private const ushort EventTypeSetHandflow = 5;
    private const ushort EventTypeSetBreak = 6;
    private const ushort EventTypePurge = 7;
    private const uint ModemControlDtr = 0x00000001;
    private const uint ModemControlRts = 0x00000002;
    private const int MaxEventBytes = 4096;
    private const int MaxRxBytes = 4096;

    private static readonly uint IoctlAttach = CtlCode(0x801);
    private static readonly uint IoctlWaitEvent = CtlCode(0x802);
    private static readonly uint IoctlPushRx = CtlCode(0x804);
    private static readonly uint IoctlSetConnectionState = CtlCode(0x805);
    private static readonly uint IoctlDetach = CtlCode(0x806);
    private static readonly uint IoctlSetModemState = CtlCode(0x807);
    private static readonly uint IoctlSetLineState = CtlCode(0x808);

    private readonly TunnelMapping _mapping;
    private readonly InMemoryLog _log;
    private readonly Action<KmdfTunnelSession, string> _faulted;
    private readonly Rfc2217Client _rfc2217 = new();
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

        await _tcp.GetStream().WriteAsync(_rfc2217.BuildInitialNegotiation(), cancellationToken).ConfigureAwait(false);

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
                if (size < EventHeaderSize || size > bytes)
                {
                    throw new InvalidOperationException($"KMDF driver returned an invalid event frame type={type}, size={size}, bytes={bytes}.");
                }

                var payloadBytes = checked((int)size - EventHeaderSize);
                var frame = BuildNetworkFrame(type, output, EventHeaderSize, payloadBytes);
                if (frame.Length > 0)
                {
                    await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
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

                var frame = _rfc2217.ProcessNetworkBytes(buffer, read);
                if (frame.Replies.Length > 0)
                {
                    await stream.WriteAsync(frame.Replies, _stop.Token).ConfigureAwait(false);
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

    private void ApplyNotification(Rfc2217Notification notification)
    {
        if (notification.Payload.Length == 0)
        {
            return;
        }

        if (notification.Command == Rfc2217Client.NotifyModemState)
        {
            var input = new byte[4];
            WriteUInt32(input, 0, MapRfc2217ModemState(notification.Payload[0]));
            DeviceIoControlChecked(_commandDriver, IoctlSetModemState, input, null);
        }
        else if (notification.Command == Rfc2217Client.NotifyLineState)
        {
            var input = new byte[4];
            WriteUInt32(input, 0, MapRfc2217LineErrors(notification.Payload[0]));
            DeviceIoControlChecked(_commandDriver, IoctlSetLineState, input, null);
        }
    }

    private static uint MapRfc2217ModemState(byte value)
    {
        const uint serialCtsState = 0x00000010;
        const uint serialDsrState = 0x00000020;
        const uint serialRiState = 0x00000040;
        const uint serialDcdState = 0x00000080;

        uint result = 0;
        if ((value & 0x10) != 0) result |= serialCtsState;
        if ((value & 0x20) != 0) result |= serialDsrState;
        if ((value & 0x40) != 0) result |= serialRiState;
        if ((value & 0x80) != 0) result |= serialDcdState;
        return result;
    }

    private static uint MapRfc2217LineErrors(byte value)
    {
        const uint serialErrorBreak = 0x00000001;
        const uint serialErrorFraming = 0x00000002;
        const uint serialErrorOverrun = 0x00000004;
        const uint serialErrorParity = 0x00000010;

        uint result = 0;
        if ((value & 0x02) != 0) result |= serialErrorOverrun;
        if ((value & 0x04) != 0) result |= serialErrorParity;
        if ((value & 0x08) != 0) result |= serialErrorFraming;
        if ((value & 0x10) != 0) result |= serialErrorBreak;
        return result;
    }

    private byte[] BuildNetworkFrame(ushort type, byte[] buffer, int offset, int length)
    {
        switch (type)
        {
            case EventTypeTxData:
                _log.Info(_mapping.Name, $"KMDF TX event {length} byte(s).");
                return Rfc2217Client.EscapeSerialData(buffer, offset, length);

            case EventTypeSetBaudRate:
                EnsurePayload(type, length, 4);
                var baudRate = ReadUInt32(buffer, offset);
                _log.Info(_mapping.Name, $"RFC2217 set baud {baudRate}.");
                return Rfc2217Client.BuildSetBaudRate(baudRate);

            case EventTypeSetLineControl:
                EnsurePayload(type, length, 4);
                var stopBits = buffer[offset];
                var parity = buffer[offset + 1];
                var wordLength = buffer[offset + 2];
                _log.Info(_mapping.Name, $"RFC2217 set line data={wordLength}, parity={parity}, stop={stopBits}.");
                return Rfc2217Client.BuildSetLineControl(stopBits, parity, wordLength);

            case EventTypeSetModemControl:
                EnsurePayload(type, length, 8);
                var mask = ReadUInt32(buffer, offset);
                bool? dtr = (mask & ModemControlDtr) != 0 ? buffer[offset + 4] != 0 : null;
                bool? rts = (mask & ModemControlRts) != 0 ? buffer[offset + 5] != 0 : null;
                _log.Info(_mapping.Name, $"RFC2217 set modem dtr={dtr?.ToString() ?? "-"}, rts={rts?.ToString() ?? "-"}.");
                return Rfc2217Client.BuildSetModemControl(dtr, rts);

            case EventTypeSetHandflow:
                EnsurePayload(type, length, 8);
                var controlHandshake = ReadUInt32(buffer, offset);
                var flowReplace = ReadUInt32(buffer, offset + 4);
                _log.Info(_mapping.Name, $"RFC2217 set handflow control=0x{controlHandshake:X8}, flow=0x{flowReplace:X8}.");
                return Rfc2217Client.BuildSetHandflow(controlHandshake, flowReplace);

            case EventTypeSetBreak:
                EnsurePayload(type, length, 4);
                var breakEnabled = buffer[offset] != 0;
                _log.Info(_mapping.Name, $"RFC2217 set break {breakEnabled}.");
                return Rfc2217Client.BuildSetBreak(breakEnabled);

            case EventTypePurge:
                EnsurePayload(type, length, 4);
                var purgeMask = ReadUInt32(buffer, offset);
                _log.Info(_mapping.Name, $"RFC2217 purge 0x{purgeMask:X8}.");
                return Rfc2217Client.BuildPurge(purgeMask);

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
