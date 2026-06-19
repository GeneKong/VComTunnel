using System.Diagnostics;
using System.IO.Compression;

namespace VComTunnel.Core;

public sealed class DependencyInstaller
{
    public const string BundledDependencyArchiveDirectoryVariable = "VCOMTUNNEL_DEPENDENCY_ARCHIVE_DIR";
    public const string Hub4comArchiveName = "hub4com-2.1.0.0-386.zip";
    public const string Com0comArchiveName = "com0com-3.0.0.0-i386-and-x64-signed.zip";
    public const string Hub4comUrl = "https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download";
    public const string Com0comUrl = "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download";
    public static readonly string[] Hub4comUrls =
    [
        "https://downloads.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip?use_mirror=cytranet",
        "https://downloads.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip",
        Hub4comUrl,
        "https://netix.dl.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip",
        "https://cytranet.dl.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip"
    ];
    public static readonly string[] Com0comUrls =
    [
        "https://downloads.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip?use_mirror=psychz",
        "https://downloads.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip",
        Com0comUrl,
        "https://netix.dl.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip",
        "https://pilotfiber.dl.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip"
    ];

    private readonly DependencyDetector _dependencyDetector;
    private readonly HttpClient _httpClient;

    public DependencyInstaller(DependencyDetector dependencyDetector, HttpClient? httpClient = null)
    {
        _dependencyDetector = dependencyDetector;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Wget/1.21.4");
        }
    }

    public async Task<DependencyInstallResult> InstallAsync(
        DependencyInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.DownloadsDirectory);
        Directory.CreateDirectory(AppPaths.ToolsDirectory);

        var steps = new List<DependencyInstallStep>();
        if (request.InstallHub4com)
        {
            steps.Add(await DownloadAndExtractAsync(
                "hub4com",
                Hub4comUrls,
                Hub4comArchiveName,
                Path.Combine(AppPaths.DownloadsDirectory, Hub4comArchiveName),
                Path.Combine(AppPaths.ToolsDirectory, "hub4com"),
                ["hub4com.exe", "com2tcp-rfc2217.bat"],
                request.Force,
                cancellationToken));
        }

        if (request.DownloadCom0com)
        {
            steps.Add(await DownloadAndExtractAnyAsync(
                "com0com",
                Com0comUrls,
                Com0comArchiveName,
                Path.Combine(AppPaths.DownloadsDirectory, Com0comArchiveName),
                Path.Combine(AppPaths.ToolsDirectory, "com0com"),
                ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe", "Setup_com0com_v3.0.0.0_W7_x86_signed.exe"],
                request.Force,
                "Prepared com0com installer package. Run the installer with administrator approval, then refresh dependencies.",
                cancellationToken));
        }

        return new DependencyInstallResult(steps, _dependencyDetector.Detect());
    }

    public string? FindCom0comInstaller()
    {
        if (!Directory.Exists(AppPaths.ToolsDirectory))
        {
            return null;
        }

        var preferredNames = Environment.Is64BitOperatingSystem
            ? new[] { "Setup_com0com_v3.0.0.0_W7_x64_signed.exe", "setup.exe", "setupg.exe", "Setup.exe", "Setup_x64.exe", "Setup_com0com_v3.0.0.0_W7_x86_signed.exe" }
            : ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe", "setup.exe", "setupg.exe", "Setup.exe"];
        foreach (var name in preferredNames)
        {
            var match = Directory.EnumerateFiles(AppPaths.ToolsDirectory, name, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return Directory.EnumerateFiles(AppPaths.ToolsDirectory, "*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(p).Contains("setup", StringComparison.OrdinalIgnoreCase));
    }

    public bool LaunchCom0comInstaller(out string message)
    {
        var installer = FindCom0comInstaller();
        if (installer is null)
        {
            message = "com0com installer was not found. Run dependency install first.";
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installer,
            UseShellExecute = true,
            Verb = "runas"
        });
        message = $"Launched {installer}";
        return true;
    }

    private async Task<DependencyInstallStep> DownloadAndExtractAsync(
        string name,
        IReadOnlyList<string> urls,
        string archiveName,
        string archivePath,
        string extractPath,
        IReadOnlyList<string> expectedFiles,
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!force && Directory.Exists(extractPath)
                && AllExpectedFilesExist(extractPath, expectedFiles))
            {
                return new DependencyInstallStep(name, true, "Already installed in VComTunnel tools cache.", extractPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            var archiveSource = await PrepareArchiveAsync(archiveName, urls, archivePath, expectedFiles, cancellationToken);

            if (force && Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

            var found = FindFirstExpected(extractPath, expectedFiles);
            if (found is null)
            {
                return new DependencyInstallStep(name, false, $"Expected files were not found after extraction: {string.Join(", ", expectedFiles)}.", extractPath);
            }

            if (!AllExpectedFilesExist(extractPath, expectedFiles))
            {
                return new DependencyInstallStep(name, false, $"Some expected files were not found after extraction: {string.Join(", ", expectedFiles)}.", extractPath);
            }

            return new DependencyInstallStep(name, true, $"{archiveSource} and extracted.", found);
        }
        catch (Exception ex)
        {
            return new DependencyInstallStep(name, false, ex.Message, extractPath);
        }
    }

    private async Task<DependencyInstallStep> DownloadAndExtractAnyAsync(
        string name,
        IReadOnlyList<string> urls,
        string archiveName,
        string archivePath,
        string extractPath,
        IReadOnlyList<string> expectedFiles,
        bool force,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!force && Directory.Exists(extractPath))
            {
                var cached = FindFirstExpected(extractPath, expectedFiles);
                if (cached is not null)
                {
                    return new DependencyInstallStep(name, true, "Already downloaded in VComTunnel tools cache.", cached);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            var archiveSource = await PrepareArchiveAsync(archiveName, urls, archivePath, expectedFiles, cancellationToken);

            if (force && Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

            var found = FindFirstExpected(extractPath, expectedFiles);
            if (found is null)
            {
                return new DependencyInstallStep(name, false, $"None of the expected files were found after extraction: {string.Join(", ", expectedFiles)}.", extractPath);
            }

            return new DependencyInstallStep(name, true, $"{archiveSource}. {successMessage}", found);
        }
        catch (Exception ex)
        {
            return new DependencyInstallStep(name, false, ex.Message, extractPath);
        }
    }

    private static string? FindFirstExpected(string extractPath, IReadOnlyList<string> expectedFiles)
    {
        foreach (var expectedFile in expectedFiles)
        {
            var found = Directory.EnumerateFiles(extractPath, expectedFile, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool AllExpectedFilesExist(string extractPath, IReadOnlyList<string> expectedFiles)
    {
        return expectedFiles.All(expectedFile =>
            Directory.EnumerateFiles(extractPath, expectedFile, SearchOption.AllDirectories).Any());
    }

    private async Task<string> PrepareArchiveAsync(
        string archiveName,
        IReadOnlyList<string> urls,
        string archivePath,
        IReadOnlyList<string> expectedFiles,
        CancellationToken cancellationToken)
    {
        var bundledArchive = FindBundledArchive(archiveName);
        if (bundledArchive is not null)
        {
            File.Copy(bundledArchive, archivePath, overwrite: true);
            ValidateArchiveContains(archivePath, expectedFiles);
            return "Installed from bundled release archive";
        }

        var failures = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                await DownloadAsync(url, archivePath, cancellationToken);
                ValidateArchiveContains(archivePath, expectedFiles);
                return $"Downloaded from {new Uri(url).Host}";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                failures.Add($"{url}: {ex.Message}");
                TryDeleteFile(archivePath);
            }
        }

        throw new InvalidDataException($"Could not download a valid dependency archive '{archiveName}'. Tried: {string.Join(" | ", failures)}");
    }

    private static void ValidateArchiveContains(string archivePath, IReadOnlyList<string> expectedFiles)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entryNames = archive.Entries
                .Select(entry => Path.GetFileName(entry.FullName.Replace('\\', '/')))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (expectedFiles.All(entryNames.Contains))
            {
                return;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"Dependency archive '{archivePath}' is not a valid zip. The download source may have returned an HTML page instead of an archive.",
                ex);
        }

        throw new InvalidDataException(
            $"Dependency archive '{archivePath}' does not contain all expected files: {string.Join(", ", expectedFiles)}.");
    }

    private static string? FindBundledArchive(string archiveName)
    {
        foreach (var directory in BundledArchiveDirectories())
        {
            var candidate = Path.Combine(directory, archiveName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> BundledArchiveDirectories()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(BundledDependencyArchiveDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            yield return overrideDirectory;
        }

        yield return AppPaths.BundledDependenciesDirectory;
        yield return Path.Combine(Environment.CurrentDirectory, "dependencies");
    }

    private async Task DownloadAsync(string url, string archivePath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(archivePath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
