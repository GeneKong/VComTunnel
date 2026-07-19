using System.Net;

namespace VComTunnel.Core;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(VComTunnelConfig config)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiblePorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backingPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in config.Mappings)
        {
            var trafficLog = mapping.TrafficLog;
            if (string.IsNullOrWhiteSpace(mapping.Id))
            {
                errors.Add("Mapping id is required.");
            }
            else if (!ids.Add(mapping.Id))
            {
                errors.Add($"Duplicate mapping id '{mapping.Id}'.");
            }

            if (!IsComPort(mapping.VisiblePort))
            {
                errors.Add($"{mapping.Name}: visiblePort must look like COM12.");
            }
            else if (!visiblePorts.Add(mapping.VisiblePort))
            {
                errors.Add($"{mapping.Name}: visiblePort '{mapping.VisiblePort}' is used more than once.");
            }

            if (mapping.Backend is TunnelBackend.Com0comHub4com or TunnelBackend.Com0comService)
            {
                if (!IsCom0comPortName(mapping.BackingPort))
                {
                    errors.Add($"{mapping.Name}: backingPort must look like COM27, CNCA0, or CNCB12 for com0com mappings.");
                }
                else if (!backingPorts.Add(mapping.BackingPort!))
                {
                    errors.Add($"{mapping.Name}: backingPort '{mapping.BackingPort}' is used more than once.");
                }

                if (string.Equals(mapping.VisiblePort, mapping.BackingPort, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{mapping.Name}: visiblePort and backingPort must be different ports.");
                }
            }

            if (mapping.Backend == TunnelBackend.Kmdf && !string.IsNullOrWhiteSpace(mapping.BackingPort))
            {
                errors.Add($"{mapping.Name}: backingPort must be empty for kmdf mappings.");
            }

            if (!IsValidHost(mapping.Host))
            {
                errors.Add($"{mapping.Name}: host is not a valid DNS name or IP address.");
            }

            if (mapping.Port is < 1 or > 65535)
            {
                errors.Add($"{mapping.Name}: port must be between 1 and 65535.");
            }

            if (mapping.Protocol != TunnelProtocol.Rfc2217)
            {
                errors.Add($"{mapping.Name}: only RFC2217 is supported.");
            }

            if (mapping.WirelessSerialAutoDiscover)
            {
                if (mapping.Backend != TunnelBackend.Com0comService)
                {
                    errors.Add($"{mapping.Name}: wirelessSerialAutoDiscover requires the com0comService backend.");
                }

                if (string.IsNullOrWhiteSpace(mapping.WirelessSerialMac))
                {
                    errors.Add($"{mapping.Name}: wirelessSerialMac is required when wirelessSerialAutoDiscover is enabled.");
                }
            }

            if (!string.IsNullOrWhiteSpace(mapping.WirelessSerialMac)
                && WirelessSerialEndpointRegistry.NormalizeMac(mapping.WirelessSerialMac) is null)
            {
                errors.Add($"{mapping.Name}: wirelessSerialMac must contain 12 hexadecimal digits.");
            }

            if (trafficLog is null)
            {
                errors.Add($"{mapping.Name}: trafficLog must not be null.");
            }
            else
            {
                if (trafficLog.Enabled && mapping.Backend != TunnelBackend.Com0comService)
                {
                    errors.Add($"{mapping.Name}: trafficLog requires the com0comService backend.");
                }

                if (trafficLog.Enabled && !trafficLog.CaptureRx && !trafficLog.CaptureTx)
                {
                    errors.Add($"{mapping.Name}: trafficLog must capture RX, TX, or both.");
                }

                if (trafficLog.Enabled
                    && trafficLog.Mode == SerialTrafficLogMode.Exclusive
                    && (!trafficLog.CaptureRx || trafficLog.CaptureTx))
                {
                    errors.Add($"{mapping.Name}: exclusive trafficLog must capture RX only.");
                }

                if (trafficLog.BaudRate is < 1 or > 4_000_000)
                {
                    errors.Add($"{mapping.Name}: trafficLog baudRate must be between 1 and 4000000.");
                }

                var directoryError = ValidateLogDirectoryPath(trafficLog.DirectoryPath);
                if (directoryError is not null)
                {
                    errors.Add($"{mapping.Name}: trafficLog directoryPath {directoryError}");
                }

                if (trafficLog.Enabled
                    && trafficLog.Format == SerialTrafficLogFormat.RawBinary
                    && trafficLog.IncludeTimestamp)
                {
                    errors.Add($"{mapping.Name}: rawBinary trafficLog cannot include timestamps.");
                }

                if (trafficLog.MaxFileSizeMb is < 1 or > 1024)
                {
                    errors.Add($"{mapping.Name}: trafficLog maxFileSizeMb must be between 1 and 1024.");
                }

                if (trafficLog.MaxFiles is < 1 or > 100)
                {
                    errors.Add($"{mapping.Name}: trafficLog maxFiles must be between 1 and 100.");
                }
            }
        }

        return errors;
    }

    public static bool IsComPort(string? port)
    {
        return !string.IsNullOrWhiteSpace(port)
            && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(port[3..], out var n)
            && n > 0;
    }

    private static bool IsCom0comPortName(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return false;
        }

        if (IsComPort(port))
        {
            return true;
        }

        return (port.StartsWith("CNCA", StringComparison.OrdinalIgnoreCase)
                || port.StartsWith("CNCB", StringComparison.OrdinalIgnoreCase))
            && int.TryParse(port[4..], out var n)
            && n >= 0;
    }

    private static string? ValidateLogDirectoryPath(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(directoryPath))
            {
                return "must be an absolute path.";
            }

            var fullPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root)
                || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return "must not be a drive or share root.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "is invalid.";
        }

        return null;
    }

    private static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns;
    }
}
