namespace VComTunnel.Core;

public sealed record Hub4comCommand(string FileName, string Arguments);

public sealed class Hub4comCommandBuilder
{
    private readonly DependencyDetector _dependencyDetector;

    public Hub4comCommandBuilder(DependencyDetector dependencyDetector)
    {
        _dependencyDetector = dependencyDetector;
    }

    public Hub4comCommand Build(TunnelMapping mapping)
    {
        if (mapping.Backend != TunnelBackend.Com0comHub4com)
        {
            throw new InvalidOperationException("hub4com is only valid for com0comHub4com mappings.");
        }

        if (string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            throw new InvalidOperationException("backingPort is required for com0comHub4com mappings.");
        }

        var com2tcp = _dependencyDetector.FindCom2TcpRfc2217()
            ?? throw new FileNotFoundException("com2tcp-rfc2217.bat was not found.");

        // Use the hub4com-provided batch wrapper. It expands to the RFC2217 client filter chain.
        var args = $"/d /c \"\"{com2tcp}\" \"\\\\.\\{mapping.BackingPort}\" {mapping.Host} {mapping.Port}\"";
        return new Hub4comCommand("cmd.exe", args);
    }

    public string BuildCom0comCreateHint(TunnelMapping mapping)
    {
        if (mapping.Backend != TunnelBackend.Com0comHub4com || string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            return "No com0com pair is needed for KMDF mappings.";
        }

        return $"setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}";
    }
}
