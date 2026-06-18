using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VComTunnel.Core;

public sealed class KmdfDeviceManager
{
    public const string HardwareId = @"Root\VComTunnelSerial";
    public const string DeviceName = "VComTunnel Virtual Serial Port";

    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");
    private static readonly Regex PortRegex = new(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IComPortInventory _ports;

    public KmdfDeviceManager()
        : this(new WindowsComPortInventory())
    {
    }

    public KmdfDeviceManager(IComPortInventory ports)
    {
        _ports = ports;
    }

    public IReadOnlyList<KmdfDeviceInfo> GetDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = RunProcess("pnputil.exe", ["/enum-devices", "/class", "Ports", "/format", "csv"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"pnputil /enum-devices failed: {result.Error}{result.Output}");
        }

        return ParsePnpUtilDevicesCsv(result.Output);
    }

    public KmdfPortOperationResult AddPort(KmdfPortRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("KMDF port management is only available on Windows.");
        }

        var portName = NormalizePortName(request.PortName);
        var devices = GetDevices();
        var existingDevice = devices.FirstOrDefault(device => PortEquals(device.PortName, portName));
        if (existingDevice is not null)
        {
            return new KmdfPortOperationResult(true, $"{portName} already exists.", existingDevice, false);
        }

        var registeredPorts = _ports.GetRegisteredPortNames();
        if (registeredPorts.Any(port => PortEquals(port, portName)))
        {
            return Fail($"{portName} is already registered by another serial device.");
        }

        var infPath = ResolveDriverInfPath(request.InfPath);
        if (infPath is null)
        {
            return Fail("VComTunnel.Serial install package was not found. Build the KMDF driver first.");
        }

        try
        {
            var instanceId = SetupApiCreateRootDevice(portName);
            var rebootRequired = InstallDriverForDevice(infPath);
            RestartDevice(instanceId);

            var created = GetDevices().FirstOrDefault(device => PortEquals(device.PortName, portName))
                ?? GetDevices().FirstOrDefault(device => string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

            return new KmdfPortOperationResult(
                created is not null,
                created is null
                    ? $"Created device {instanceId}, but {portName} was not detected yet. Try Refresh or check Device Manager."
                    : $"Created {portName}.",
                created,
                rebootRequired);
        }
        catch (Exception ex)
        {
            return Fail($"Create {portName} failed: {ex.Message}");
        }
    }

    public KmdfPortOperationResult RemovePort(KmdfPortRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("KMDF port management is only available on Windows.");
        }

        var devices = GetDevices();
        var device = !string.IsNullOrWhiteSpace(request.InstanceId)
            ? devices.FirstOrDefault(candidate => string.Equals(candidate.InstanceId, request.InstanceId, StringComparison.OrdinalIgnoreCase))
            : devices.FirstOrDefault(candidate => PortEquals(candidate.PortName, NormalizePortName(request.PortName)));

        if (device is null)
        {
            return Fail("KMDF port was not found.");
        }

        var result = RunProcess("pnputil.exe", ["/remove-device", device.InstanceId, "/force"]);
        if (result.ExitCode != 0)
        {
            return Fail($"Remove {device.PortName} failed: {result.Error}{result.Output}");
        }

        return new KmdfPortOperationResult(true, $"Removed {device.PortName}.", device, false);
    }

    public static IReadOnlyList<KmdfDeviceInfo> ParsePnpUtilDevicesCsv(string csv)
    {
        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        var header = ParseCsvLine(lines[0]);
        var indexes = header
            .Select((name, index) => new { name, index })
            .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);

        var devices = new List<KmdfDeviceInfo>();
        foreach (var line in lines.Skip(1))
        {
            var columns = ParseCsvLine(line);
            var manufacturer = GetColumn(columns, indexes, "ManufacturerName");
            var description = GetColumn(columns, indexes, "DeviceDescription");
            if (!string.Equals(manufacturer, "VComTunnel", StringComparison.OrdinalIgnoreCase)
                && !description.StartsWith(DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = PortRegex.Match(description);
            var portName = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
            devices.Add(new KmdfDeviceInfo(
                portName,
                GetColumn(columns, indexes, "InstanceId"),
                GetColumn(columns, indexes, "Status"),
                EmptyToNull(GetColumn(columns, indexes, "DriverName")),
                EmptyToNull(GetColumn(columns, indexes, "ProblemCode")),
                string.Equals(GetColumn(columns, indexes, "Status"), "Started", StringComparison.OrdinalIgnoreCase)));
        }

        return devices
            .OrderBy(device => TryGetPortNumber(device.PortName, out var number) ? number : int.MaxValue)
            .ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizePortName(string? portName)
    {
        if (!TryGetPortNumber(portName, out var portNumber))
        {
            throw new ArgumentException("Port name must look like COM27.", nameof(portName));
        }

        return $"COM{portNumber}";
    }

    public static string? ResolveDriverInfPath(string? explicitInfPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitInfPath))
        {
            var full = Path.GetFullPath(explicitInfPath);
            return IsInstallableInf(full) ? full : null;
        }

        foreach (var root in CandidateRoots())
        {
            var candidates = new[]
            {
                Path.Combine(root, "drivers", "VComTunnel.Serial", "x64", "Release", "VComTunnel.Serial", "VComTunnel.Serial.inf"),
                Path.Combine(root, "drivers", "VComTunnel.Serial", "x64", "Release", "VComTunnel.Serial.inf")
            };

            foreach (var candidate in candidates)
            {
                if (IsInstallableInf(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool InstallDriverForDevice(string infPath)
    {
        if (!UpdateDriverForPlugAndPlayDevices(
                IntPtr.Zero,
                HardwareId,
                Path.GetFullPath(infPath),
                InstallFlagForce,
                out var rebootRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateDriverForPlugAndPlayDevices failed.");
        }

        return rebootRequired;
    }

    private static string SetupApiCreateRootDevice(string portName)
    {
        var classGuid = PortsClassGuid;
        var infoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (infoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList failed.");
        }

        try
        {
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            if (!SetupDiCreateDeviceInfo(
                infoSet,
                DeviceName,
                ref classGuid,
                null,
                IntPtr.Zero,
                DicdGenerateId,
                    ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo failed.");
            }

            SetDeviceRegistryProperty(infoSet, ref info, SpdrpHardwareId, ToMultiSz(HardwareId));
            SetDeviceRegistryProperty(infoSet, ref info, SpdrpDeviceDesc, ToRegSz(DeviceName));
            SetDeviceRegistryProperty(infoSet, ref info, SpdrpFriendlyName, ToRegSz($"{DeviceName} ({portName})"));

            if (!SetupDiCallClassInstaller(DifRegisterDevice, infoSet, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCallClassInstaller(DIF_REGISTERDEVICE) failed.");
            }

            SetDevicePortName(infoSet, ref info, portName);
            return GetDeviceInstanceId(infoSet, ref info);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    private static void SetDevicePortName(IntPtr infoSet, ref SpDevInfoData info, string portName)
    {
        var key = SetupDiOpenDevRegKey(infoSet, ref info, DicsFlagGlobal, 0, DiregDev, KeySetValue);
        if (key == IntPtr.Zero || key == InvalidHandleValue)
        {
            key = SetupDiCreateDevRegKey(infoSet, ref info, DicsFlagGlobal, 0, DiregDev, null, IntPtr.Zero);
        }

        if (key == IntPtr.Zero || key == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDevRegKey failed.");
        }

        try
        {
            RegSetStringValue(key, "PortName", portName);
            RegSetStringValue(key, "FriendlyName", $"{DeviceName} ({portName})");
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void RestartDevice(string instanceId)
    {
        var result = RunProcess("pnputil.exe", ["/restart-device", instanceId]);
        if (result.ExitCode != 0)
        {
            RunProcess("pnputil.exe", ["/scan-devices"]);
        }
    }

    private static string GetDeviceInstanceId(IntPtr infoSet, ref SpDevInfoData info)
    {
        var builder = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(infoSet, ref info, builder, builder.Capacity, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInstanceId failed.");
        }

        return builder.ToString();
    }

    private static void SetDeviceRegistryProperty(IntPtr infoSet, ref SpDevInfoData info, uint property, byte[] value)
    {
        if (!SetupDiSetDeviceRegistryProperty(infoSet, ref info, property, value, value.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetDeviceRegistryProperty failed.");
        }
    }

    private static void RegSetStringValue(IntPtr key, string name, string value)
    {
        var bytes = ToRegSz(value);
        var status = RegSetValueEx(key, name, 0, RegSz, bytes, bytes.Length);
        if (status != 0)
        {
            throw new Win32Exception(status, $"RegSetValueEx({name}) failed.");
        }
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == ',')
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static string GetColumn(IReadOnlyList<string> columns, IReadOnlyDictionary<string, int> indexes, string name)
    {
        return indexes.TryGetValue(name, out var index) && index >= 0 && index < columns.Count
            ? columns[index]
            : "";
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryGetPortNumber(string? portName, out int portNumber)
    {
        portNumber = 0;
        return !string.IsNullOrWhiteSpace(portName)
            && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(portName[3..], out portNumber)
            && portNumber > 0;
    }

    private static bool PortEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static KmdfPortOperationResult Fail(string message)
    {
        return new KmdfPortOperationResult(false, message, null, false);
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }

    private static bool IsInstallableInf(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var root = Path.GetDirectoryName(path);
        return root is not null
            && File.Exists(Path.Combine(root, "VComTunnel.Serial.sys"))
            && Directory.EnumerateFiles(root, "*.cat").Any();
    }

    private static byte[] ToRegSz(string value)
    {
        return Encoding.Unicode.GetBytes(value + "\0");
    }

    private static byte[] ToMultiSz(string value)
    {
        return Encoding.Unicode.GetBytes(value + "\0\0");
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint DicdGenerateId = 0x00000001;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const int KeySetValue = 0x0002;
    private const uint RegSz = 1;
    private const uint InstallFlagForce = 0x00000001;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceName,
        ref Guid classGuid,
        string? deviceDescription,
        IntPtr hwndParent,
        uint creationFlags,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        int propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiCreateDevRegKey(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint scope,
        uint hwProfile,
        uint keyType,
        string? infSectionName,
        IntPtr infHandle);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint scope,
        uint hwProfile,
        uint keyType,
        int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(
        IntPtr key,
        string valueName,
        int reserved,
        uint type,
        byte[] data,
        int dataSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr key);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out bool rebootRequired);
}
