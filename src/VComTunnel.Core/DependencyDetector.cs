namespace VComTunnel.Core;

public sealed class DependencyDetector
{
    private readonly IReadOnlyList<string> _candidateRoots;
    private readonly string? _pathOverride;

    public DependencyDetector(IEnumerable<string>? candidateRoots = null, string? pathOverride = null)
    {
        var roots = candidateRoots?.ToArray();
        _candidateRoots = roots is { Length: > 0 } ? roots : CreateDefaultCandidateRoots();
        _pathOverride = pathOverride;
    }

    public SystemDependencyReport Detect()
    {
        var setupc = FindSetupc();
        var hub4com = FindExecutable("hub4com.exe");
        var com2tcp = FindExecutable("com2tcp-rfc2217.bat");
        var pnputil = FindOnPath("pnputil.exe");

        var items = new List<DependencyStatus>
        {
            ToStatus("com0com setupc.exe", setupc, "Install com0com and make setupc.exe available."),
            ToStatus("hub4com.exe", hub4com, "Install hub4com and make hub4com.exe available."),
            ToStatus("com2tcp-rfc2217.bat", com2tcp, "Optional legacy hub4com wrapper; the default bridge path uses hub4com.exe directly without control-line filters."),
            ToStatus("pnputil.exe", pnputil, "Windows pnputil is required for manual KMDF driver install scripts.")
        };

        return new SystemDependencyReport(
            items,
            setupc is not null && hub4com is not null,
            pnputil is not null);
    }

    public IReadOnlyList<string> CandidateRoots => _candidateRoots;

    public string? FindHub4com() => FindExecutable("hub4com.exe");

    public string? FindCom2TcpRfc2217() => FindExecutable("com2tcp-rfc2217.bat");

    public string? FindSetupc() => FindExecutable("setupc.exe", includeToolsCache: false);

    private static DependencyStatus ToStatus(string name, string? path, string missingMessage)
    {
        return new DependencyStatus(name, path is not null, path, path is null ? missingMessage : "Found.");
    }

    private string? FindExecutable(string name, bool includeToolsCache = true)
    {
        var pathMatch = FindOnPath(name);
        if (pathMatch is not null)
        {
            return pathMatch;
        }

        foreach (var root in _candidateRoots.Where(Directory.Exists))
        {
            if (!includeToolsCache && IsToolsCacheRoot(root))
            {
                continue;
            }

            var found = TryFindUnderRoot(root, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? TryFindUnderRoot(string root, string name)
    {
        try
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct))
            {
                return direct;
            }

            return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsToolsCacheRoot(string root)
    {
        try
        {
            var candidate = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tools = Path.GetFullPath(AppPaths.ToolsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, tools, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(tools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(tools + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string? FindOnPath(string name)
    {
        var path = _pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] CreateDefaultCandidateRoots()
    {
        return
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            AppPaths.ToolsDirectory,
            @"C:\Program Files\com0com",
            @"C:\Program Files (x86)\com0com",
            @"C:\Program Files\hub4com",
            @"C:\Program Files (x86)\hub4com"
        ];
    }
}
