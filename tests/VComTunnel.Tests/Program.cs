using VComTunnel.Core;
using System.IO.Compression;
using System.Net;

var tests = new List<(string Name, Func<Task> Test)>
{
    ("valid multi mapping config", () => Task.Run(ValidMultiMappingConfig)),
    ("duplicate visible COM is rejected", () => Task.Run(DuplicateVisibleComIsRejected)),
    ("same visible and backing COM is rejected", () => Task.Run(SameVisibleAndBackingComIsRejected)),
    ("KMDF backing port is rejected", () => Task.Run(KmdfBackingPortIsRejected)),
    ("invalid host and port are rejected", () => Task.Run(InvalidHostAndPortAreRejected)),
    ("COM backing port is accepted", () => Task.Run(ComBackingPortIsAccepted)),
    ("config round trip", ConfigRoundTripAsync),
    ("com0com create hints", () => Task.Run(Com0comCreateHints)),
    ("com2tcp command uses batch wrapper", () => Task.Run(Com2TcpCommandUsesBatchWrapper)),
    ("missing dependencies fault mapping", MissingDependenciesFaultMappingAsync),
    ("missing backing port faults before hub4com", MissingBackingPortFaultsBeforeHub4comAsync),
    ("com0com create and remove plans", Com0comCreateAndRemovePlansAsync),
    ("KMDF mapping faults when driver is missing", KmdfMappingFaultsWhenDriverIsMissingAsync),
    ("fake com2tcp process starts and stops", FakeCom2TcpProcessStartsAndStopsAsync),
    ("dependency installer extracts tool zips", DependencyInstallerExtractsToolZipsAsync),
    ("dependency installer uses bundled release archives", DependencyInstallerUsesBundledReleaseArchivesAsync)
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ValidMultiMappingConfig()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "esp-dap.local", Port = 3333 },
            new TunnelMapping { Name = "B", Backend = TunnelBackend.Kmdf, VisiblePort = "COM22", BackingPort = null, Host = "192.168.1.50", Port = 3333 }
        ]
    };

    AssertEmpty(ConfigValidator.Validate(config));
}

static void DuplicateVisibleComIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "127.0.0.1", Port = 3333 },
            new TunnelMapping { Name = "B", VisiblePort = "COM12", BackingPort = "CNCB13", Host = "127.0.0.1", Port = 3334 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "used more than once");
}

static void SameVisibleAndBackingComIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Loop", VisiblePort = "COM30", BackingPort = "COM30", Host = "127.0.0.1", Port = 3333 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "must be different");
}

static void KmdfBackingPortIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "K", Backend = TunnelBackend.Kmdf, VisiblePort = "COM22", BackingPort = "CNCB22", Host = "127.0.0.1", Port = 3333 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "backingPort must be empty");
}

static void InvalidHostAndPortAreRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Bad", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "bad host name", Port = 70000 }
        ]
    };

    var errors = ConfigValidator.Validate(config);
    AssertContains(errors, "host");
    AssertContains(errors, "port");
}

static void ComBackingPortIsAccepted()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "ExistingPair", VisiblePort = "COM28", BackingPort = "COM27", Host = "127.0.0.1", Port = 4000 }
        ]
    };

    AssertEmpty(ConfigValidator.Validate(config));
}

static async Task ConfigRoundTripAsync()
{
    using var temp = new TempDir();
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "RoundTrip", VisiblePort = "COM33", BackingPort = "CNCB33", Host = "127.0.0.1", Port = 2217, AutoStart = true }
        ]
    };

    await store.SaveAsync(config);
    var loaded = await store.LoadAsync();
    AssertEqual("RoundTrip", loaded.Mappings.Single().Name);
    AssertEqual("COM33", loaded.Mappings.Single().VisiblePort);
    AssertTrue(loaded.Mappings.Single().AutoStart, "AutoStart should round-trip.");
}

static void Com0comCreateHints()
{
    var mapping = new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12" };
    var hint = new Hub4comCommandBuilder(new DependencyDetector()).BuildCom0comCreateHint(mapping);
    AssertEqual("setupc.exe install PortName=COM12 PortName=CNCB12", hint);
}

static void Com2TcpCommandUsesBatchWrapper()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var command = new Hub4comCommandBuilder(detector).Build(new TunnelMapping
    {
        BackingPort = "CNCB12",
        Host = "192.168.1.50",
        Port = 3333
    });

    AssertEqual("cmd.exe", command.FileName);
    AssertStringContains(command.Arguments, "com2tcp-rfc2217.bat");
    AssertStringContains(command.Arguments, "\\\\.\\CNCB12");
    AssertStringContains(command.Arguments, "192.168.1.50 3333");
}

static async Task MissingDependenciesFaultMappingAsync()
{
    using var temp = new TempDir();
    var store = await StoreWithMappingAsync(temp.Path, new TunnelMapping { Name = "Missing", Host = "127.0.0.1" });
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([Path.Combine(temp.Path, "missing")], pathOverride: ""), new InMemoryLog());

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "dependencies are missing");
}

static async Task MissingBackingPortFaultsBeforeHub4comAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Bad backing",
        VisiblePort = "COM27",
        BackingPort = "CNCB27",
        Host = "127.0.0.1",
        Port = 4000
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        new InMemoryLog(),
        ["COM27", "COM28"]);

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "Backing port CNCB27 is not registered");
    AssertStringContains(status.LastError ?? "", "Existing ports: COM27, COM28");
}

static async Task KmdfMappingFaultsWhenDriverIsMissingAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM44",
        BackingPort = null
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([temp.Path], pathOverride: ""), new InMemoryLog());

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "Could not open KMDF virtual serial port");
}

static async Task Com0comCreateAndRemovePlansAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Managed",
        VisiblePort = "COM29",
        BackingPort = "CNCB29",
        Host = "127.0.0.1",
        Port = 4000
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var manager = new Com0comSetupManager(
        store,
        detector,
        new FakeComPortInventory(["COM29", "CNCB29"], [new Com0comPairInfo(2, "COM29", "CNCB29", @"\Device\com0com12", @"\Device\com0com22", true)]));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var create = await manager.BuildCreatePlanAsync(id);
    AssertStringContains(create.Arguments, "install PortName=COM29 PortName=CNCB29");
    AssertTrue(create.RequiresElevation, "setupc plans should require elevation.");

    var remove = manager.BuildRemovePlan(2);
    AssertStringContains(remove.Arguments, "remove 2");
    AssertEqual(1.ToString(), manager.GetPairs().Count.ToString());
}

static async Task FakeCom2TcpProcessStartsAndStopsAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Fake bridge",
        VisiblePort = "COM55",
        BackingPort = "CNCB55",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = false
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([temp.Path], pathOverride: ""), log);
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    AssertTrue(started.ProcessId is not null, "Process id should be reported.");
    await Task.Delay(500);
    AssertTrue(log.Snapshot().Any(e => e.Message.Contains("fake-com2tcp", StringComparison.OrdinalIgnoreCase)), "Fake process output should be logged.");

    var stopped = orchestrator.Stop(id);
    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
}

static async Task DependencyInstallerExtractsToolZipsAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", temp.Path);
    try
    {
        var http = new HttpClient(new ZipHandler())
        {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest());

        AssertTrue(result.Steps.All(s => s.Success), string.Join("; ", result.Steps.Select(s => s.Message)));
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com", "com2tcp-rfc2217.bat")), "hub4com batch should be extracted.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "com0com", "Setup_com0com_v3.0.0.0_W7_x64_signed.exe")), "com0com installer should be extracted.");
        AssertTrue(!detector.Detect().IsReadyForCom0comHub4com, "Detector should remain not ready until com0com driver tools are installed.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
    }
}

static async Task DependencyInstallerUsesBundledReleaseArchivesAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    var oldArchiveRoot = Environment.GetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable);
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", Path.Combine(temp.Path, "home"));
    Environment.SetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable, Path.Combine(temp.Path, "bundled"));
    try
    {
        var archiveRoot = Environment.GetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable)!;
        Directory.CreateDirectory(archiveRoot);
        CreateZip(
            Path.Combine(archiveRoot, DependencyInstaller.Hub4comArchiveName),
            new Dictionary<string, string> { ["hub4com.exe"] = "", ["com2tcp-rfc2217.bat"] = "@echo off" });
        CreateZip(
            Path.Combine(archiveRoot, DependencyInstaller.Com0comArchiveName),
            new Dictionary<string, string> { ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "", ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = "" });

        var http = new HttpClient(new ThrowingHandler());
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest());

        AssertTrue(result.Steps.All(s => s.Success), string.Join("; ", result.Steps.Select(s => s.Message)));
        AssertTrue(result.Steps.All(s => s.Message.Contains("bundled release archive", StringComparison.OrdinalIgnoreCase)), "Bundled archives should be used before network downloads.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com", "com2tcp-rfc2217.bat")), "hub4com batch should be extracted from bundled archive.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "com0com", "Setup_com0com_v3.0.0.0_W7_x64_signed.exe")), "com0com installer should be extracted from bundled archive.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
        Environment.SetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable, oldArchiveRoot);
    }
}

static async Task<ConfigStore> StoreWithMappingAsync(string root, TunnelMapping mapping)
{
    var store = new ConfigStore(Path.Combine(root, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [mapping] });
    return store;
}

static TunnelOrchestrator CreateOrchestrator(ConfigStore store, DependencyDetector detector, InMemoryLog log)
{
    return CreateOrchestratorWithPorts(store, detector, log, []);
}

static TunnelOrchestrator CreateOrchestratorWithPorts(ConfigStore store, DependencyDetector detector, InMemoryLog log, IReadOnlyList<string>? registeredPorts)
{
    return new TunnelOrchestrator(store, detector, new Hub4comCommandBuilder(detector), new FakeComPortInventory(registeredPorts), log);
}

static void CreateFakeDependencies(string root)
{
    File.WriteAllText(Path.Combine(root, "setupc.exe"), "");
    File.WriteAllText(Path.Combine(root, "hub4com.exe"), "");
    File.WriteAllText(
        Path.Combine(root, "com2tcp-rfc2217.bat"),
        """
        @echo off
        echo fake-com2tcp %*
        ping -n 4 127.0.0.1 > nul
        """);
}

static void AssertEmpty(IReadOnlyList<string> errors)
{
    if (errors.Count != 0)
    {
        throw new Exception(string.Join("; ", errors));
    }
}

static void AssertContains(IReadOnlyList<string> errors, string expected)
{
    if (!errors.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception($"Expected '{expected}' in: {string.Join("; ", errors)}");
    }
}

static void AssertStringContains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception($"Expected '{expected}' in '{actual}'.");
    }
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void CreateZip(string path, IReadOnlyDictionary<string, string> files)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (name, content) in files)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VComTunnelTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class ZipHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isHub4com = request.RequestUri?.ToString().Contains("hub4com", StringComparison.OrdinalIgnoreCase) == true;
        var files = isHub4com
            ? new Dictionary<string, string> { ["hub4com.exe"] = "", ["com2tcp-rfc2217.bat"] = "@echo off" }
            : new Dictionary<string, string> { ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "", ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = "" };

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        return Task.FromResult(response);
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Network should not be used when bundled dependency archives are available.");
    }
}

internal sealed class FakeComPortInventory : IComPortInventory
{
    private readonly IReadOnlyList<string> _ports;
    private readonly IReadOnlyList<Com0comPairInfo> _pairs;

    public FakeComPortInventory(IReadOnlyList<string>? ports, IReadOnlyList<Com0comPairInfo>? pairs = null)
    {
        _ports = ports ?? [];
        _pairs = pairs ?? [];
    }

    public IReadOnlyList<string> GetRegisteredPortNames() => _ports;
    public IReadOnlyList<Com0comPairInfo> GetCom0comPairs() => _pairs;
}
