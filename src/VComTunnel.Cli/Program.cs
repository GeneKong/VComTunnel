using System.Diagnostics;
using VComTunnel.Core;

var exitCode = await VComTunnelCtl.RunAsync(args);
return exitCode;

internal static class VComTunnelCtl
{
    private const string ServiceName = "VComTunnel";
    private const string ServiceBaseUrl = "http://127.0.0.1:44817";

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return command switch
        {
            "diagnose" => Diagnose(),
            "init-config" => await InitConfigAsync(),
            "create-hints" => await CreateHintsAsync(),
            "status" => await GetAsync("/api/status"),
            "dependencies" => await GetAsync("/api/dependencies"),
            "mappings" => await GetAsync("/api/mappings"),
            "ports" => await GetAsync("/api/com0com/pairs"),
            "pair" => await PairAsync(args.Skip(1).ToArray()),
            "start" => await PostMappingAsync(args.Skip(1).FirstOrDefault(), "start"),
            "stop" => await PostMappingAsync(args.Skip(1).FirstOrDefault(), "stop"),
            "logs" => await GetAsync("/api/logs"),
            "deps" => await DepsAsync(args.Skip(1).ToArray()),
            "service" => Service(args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(command)
        };
    }

    private static int Diagnose()
    {
        var report = new DependencyDetector().Detect();
        Console.WriteLine("VComTunnel dependency report");
        Console.WriteLine($"Config path: {AppPaths.ConfigPath}");
        foreach (var item in report.Items)
        {
            Console.WriteLine($"- {(item.Found ? "OK " : "MISS")} {item.Name}: {item.Path ?? item.Message}");
        }

        Console.WriteLine($"com0com/hub4com ready: {report.IsReadyForCom0comHub4com}");
        Console.WriteLine($"KMDF install tooling ready: {report.IsReadyForKmdf}");
        return report.IsReadyForCom0comHub4com ? 0 : 2;
    }

    private static async Task<int> InitConfigAsync()
    {
        var store = new ConfigStore();
        if (File.Exists(store.Path))
        {
            Console.WriteLine($"Config already exists: {store.Path}");
            return 0;
        }

        var sample = new VComTunnelConfig
        {
            Mappings =
            [
                new TunnelMapping
                {
                    Name = "ESP-DAP 1",
                    VisiblePort = "COM12",
                    BackingPort = "CNCB12",
                    Host = "192.168.1.50",
                    Port = 3333
                }
            ]
        };

        await store.SaveAsync(sample);
        Console.WriteLine($"Created sample config: {store.Path}");
        return 0;
    }

    private static async Task<int> CreateHintsAsync()
    {
        var config = await new ConfigStore().LoadAsync();
        var builder = new Hub4comCommandBuilder(new DependencyDetector());

        foreach (var mapping in config.Mappings)
        {
            Console.WriteLine($"{mapping.Name}: {builder.BuildCom0comCreateHint(mapping)}");
        }

        return 0;
    }

    private static async Task<int> GetAsync(string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ServiceBaseUrl) };
        try
        {
            var response = await client.GetAsync(path);
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Service is not reachable at {ServiceBaseUrl}: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> PostMappingAsync(string? id, string action)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.WriteLine($"Usage: vcomtunnelctl {action} <mappingId>");
            return 2;
        }

        using var client = new HttpClient { BaseAddress = new Uri(ServiceBaseUrl) };
        try
        {
            var response = await client.PostAsync($"/api/mappings/{id}/{action}", null);
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Service is not reachable at {ServiceBaseUrl}: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> PairAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "create-plan" => await PairPlanAsync($"/api/com0com/mappings/{args.Skip(1).FirstOrDefault()}/create-plan"),
            "remove-plan" => await PairPlanAsync($"/api/com0com/pairs/{args.Skip(1).FirstOrDefault()}/remove-plan"),
            _ => PairHelp()
        };
    }

    private static async Task<int> PairPlanAsync(string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ServiceBaseUrl) };
        try
        {
            var response = await client.PostAsync(path, null);
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Service is not reachable at {ServiceBaseUrl}: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> DepsAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "install" => await InstallDepsAsync(args),
            "launch-com0com" => LaunchCom0comInstaller(),
            _ => DepsHelp()
        };
    }

    private static async Task<int> InstallDepsAsync(string[] args)
    {
        var request = new DependencyInstallRequest(
            InstallHub4com: !args.Contains("--no-hub4com", StringComparer.OrdinalIgnoreCase),
            DownloadCom0com: !args.Contains("--no-com0com", StringComparer.OrdinalIgnoreCase),
            Force: args.Contains("--force", StringComparer.OrdinalIgnoreCase));

        var installer = new DependencyInstaller(new DependencyDetector());
        var result = await installer.InstallAsync(request);
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"{(step.Success ? "OK" : "FAIL")} {step.Name}: {step.Message}");
            if (step.Path is not null)
            {
                Console.WriteLine($"  {step.Path}");
            }
        }

        Console.WriteLine($"com0com/hub4com ready: {result.DependencyReport.IsReadyForCom0comHub4com}");
        Console.WriteLine("If com0com is downloaded but not installed, run: vcomtunnelctl deps launch-com0com");
        return result.Steps.All(s => s.Success) ? 0 : 2;
    }

    private static int LaunchCom0comInstaller()
    {
        var installer = new DependencyInstaller(new DependencyDetector());
        if (installer.LaunchCom0comInstaller(out var message))
        {
            Console.WriteLine(message);
            return 0;
        }

        Console.WriteLine(message);
        return 2;
    }

    private static int Service(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "install" => InstallService(args.Skip(1).FirstOrDefault()),
            "uninstall" => RunSc("delete", ServiceName),
            "start" => RunSc("start", ServiceName),
            "stop" => RunSc("stop", ServiceName),
            _ => ServiceHelp()
        };
    }

    private static int InstallService(string? explicitServicePath)
    {
        var servicePath = ResolveServiceExe(explicitServicePath);
        if (servicePath is null)
        {
            Console.WriteLine("Could not locate VComTunnel.Service.exe.");
            Console.WriteLine("Usage: vcomtunnelctl service install C:\\path\\to\\VComTunnel.Service.exe");
            return 2;
        }

        Console.WriteLine("Installing Windows service through sc.exe.");
        Console.WriteLine("The service app can also be run directly for console-mode debugging.");
        return RunSc("create", ServiceName, "binPath=", servicePath, "start=", "auto", "DisplayName=", "VComTunnel");
    }

    private static string? ResolveServiceExe(string? explicitServicePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitServicePath))
        {
            var full = Path.GetFullPath(explicitServicePath);
            return File.Exists(full) ? full : null;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "VComTunnel.Service.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VComTunnel.Service", "bin", "Debug", "net8.0-windows", "VComTunnel.Service.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VComTunnel.Service", "bin", "Release", "net8.0-windows", "VComTunnel.Service.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int RunSc(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static int Help()
    {
        Console.WriteLine("""
        vcomtunnelctl commands:
          diagnose                 Check com0com, hub4com, com2tcp-rfc2217, pnputil
          init-config              Create a sample multi-mapping config
          create-hints             Print setupc.exe commands for com0com pairs
          status                   Read /api/status from the local service
          dependencies             Read /api/dependencies from the local service
          mappings                 Read /api/mappings from the local service
          ports                    List registered com0com pairs
          pair create-plan <id>    Print setupc plan for one mapping
          pair remove-plan <n>     Print setupc remove plan for pair number n
          start <mappingId>        Start one configured mapping
          stop <mappingId>         Stop one configured mapping
          logs                     Read /api/logs from the local service
          deps install [--force] [--no-hub4com] [--no-com0com]
          deps launch-com0com      Launch downloaded com0com installer with UAC
          service install [serviceExe] | uninstall | start | stop
        """);
        return 0;
    }

    private static int DepsHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl deps install [--force] [--no-hub4com] [--no-com0com] | launch-com0com");
        return 0;
    }

    private static int ServiceHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl service install [serviceExe] | uninstall | start | stop");
        return 0;
    }

    private static int PairHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl pair create-plan <mappingId> | remove-plan <pairNumber>");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        return Help();
    }
}
