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

        var hub4com = _dependencyDetector.FindHub4com()
            ?? throw new FileNotFoundException("hub4com.exe was not found.");

        var args = mapping.Hub4comForwardControlLines
            ? BuildControlLineForwardingArgs(mapping)
            : BuildNoControlLineArgs(mapping);

        return new Hub4comCommand(hub4com, args);
    }

    public string BuildCom0comCreateHint(TunnelMapping mapping)
    {
        if (mapping.Backend is not (TunnelBackend.Com0comHub4com or TunnelBackend.Com0comService)
            || string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            return "No com0com pair is needed for KMDF mappings.";
        }

        return $"setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}";
    }

    private static string BuildNoControlLineArgs(TunnelMapping mapping)
    {
        // Default bridge mode intentionally omits pinmap/linectl filters so opening
        // a tunnel cannot reset a target or put it into a bootloader through DTR/RTS.
        return string.Join(
            " ",
            "--create-filter=escparse,com,parse",
            "--add-filters=0:com",
            "--create-filter=telnet,tcp,telnet:\" --comport=client\"",
            "--add-filters=1:tcp",
            "--octs=off",
            Quote($@"\\.\{mapping.BackingPort}"),
            "--use-driver=tcp",
            Quote($"*{mapping.Host}:{mapping.Port}"));
    }

    private static string BuildControlLineForwardingArgs(TunnelMapping mapping)
    {
        return string.Join(
            " ",
            "--create-filter=escparse,com,parse",
            "--create-filter=pinmap,com,pinmap:\"--rts=cts --dtr=dsr\"",
            "--create-filter=linectl,com,lc:\"--br=local --lc=local\"",
            "--add-filters=0:com",
            "--create-filter=telnet,tcp,telnet:\" --comport=client\"",
            "--create-filter=pinmap,tcp,pinmap:\"--rts=cts --dtr=dsr --break=break\"",
            "--create-filter=linectl,tcp,lc:\"--br=remote --lc=remote\"",
            "--add-filters=1:tcp",
            "--octs=off",
            Quote($@"\\.\{mapping.BackingPort}"),
            "--use-driver=tcp",
            Quote($"*{mapping.Host}:{mapping.Port}"));
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
