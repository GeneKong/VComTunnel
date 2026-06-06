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

            if (mapping.Backend == TunnelBackend.Com0comHub4com)
            {
                if (!IsCom0comPortName(mapping.BackingPort))
                {
                    errors.Add($"{mapping.Name}: backingPort must look like COM27, CNCA0, or CNCB12 for com0com/hub4com.");
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
