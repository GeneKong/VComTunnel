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
    private readonly CancellationTokenSource _stop = new();
    private SafeFileHandle? _driver;
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

        _driver = OpenDriver(_mapping.VisiblePort);
        Attach();

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_mapping.Host, _mapping.Port, cancellationToken).ConfigureAwait(false);

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

        try
        {
            if (_driver is { IsInvalid: false, IsClosed: false })
            {
                DeviceIoControl(_driver, IoctlDetach, null, 0, null, 0, out _, IntPtr.Zero);
            }
        }
        catch
        {
        }

        _tcp?.Dispose();
        _driver?.Dispose();
        _stop.Dispose();
    }

    private static SafeFileHandle OpenDriver(string visiblePort)
    {
        var path = @"\\.\" + visiblePort.Trim();
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open KMDF virtual serial port {path}.");
        }

        return handle;
    }

    private void Attach()
    {
        var input = new byte[136];
        WriteUInt16(input, 0, 1);
        WriteUInt16(input, 2, 0);
        var instance = Encoding.Unicode.GetBytes(Environment.MachineName);
        Array.Copy(instance, 0, input, 8, Math.Min(instance.Length, 126));

        var output = new byte[80];
        DeviceIoControlChecked(IoctlAttach, input, output);
    }

    private async Task EventLoopAsync()
    {
        try
        {
            var stream = _tcp!.GetStream();
            var output = new byte[MaxEventBytes];

            while (!_stop.IsCancellationRequested)
            {
                var bytes = DeviceIoControlChecked(IoctlWaitEvent, null, output);
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
                await stream.WriteAsync(output.AsMemory(EventHeaderSize, payloadBytes), _stop.Token).ConfigureAwait(false);
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

                var push = new byte[8 + read];
                WriteUInt32(push, 0, 0);
                WriteUInt32(push, 4, (uint)read);
                Buffer.BlockCopy(buffer, 0, push, 8, read);
                DeviceIoControlChecked(IoctlPushRx, push, null);
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
        if (_driver is null || _driver.IsInvalid || _driver.IsClosed)
        {
            return;
        }

        var input = new byte[4];
        WriteUInt32(input, 0, state);
        DeviceIoControl(_driver, IoctlSetConnectionState, input, input.Length, null, 0, out _, IntPtr.Zero);
    }

    private int DeviceIoControlChecked(uint ioctl, byte[]? input, byte[]? output)
    {
        if (_driver is null)
        {
            throw new InvalidOperationException("KMDF driver is not open.");
        }

        var ok = DeviceIoControl(
            _driver,
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
