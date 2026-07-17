using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using VComTunnel.Core;

var exitCode = await VComTunnelCtl.RunAsync(args);
return exitCode;

internal static class VComTunnelCtl
{
    private const string ServiceName = "VComTunnel";

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
            "wireless-serial" or "wireless" => await WirelessSerialAsync(args.Skip(1).ToArray()),
            "pair" => await PairAsync(args.Skip(1).ToArray()),
            "kmdf" => Kmdf(args.Skip(1).ToArray()),
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

    private static CliOptions ParseOptions(string[] args)
    {
        var positionals = new List<string>();
        string? resultFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--result-file", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                resultFile = args[++i];
                continue;
            }

            positionals.Add(args[i]);
        }

        return new CliOptions(positionals.ToArray(), resultFile);
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
                    Port = TunnelMapping.DefaultRfc2217Port,
                    Hub4comForwardControlLines = true
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
        if (!TryCreateServiceClient(out var client, out var serviceBaseUrl))
        {
            return 2;
        }

        using (client)
        {
            try
            {
                var response = await client.GetAsync(path);
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                return response.IsSuccessStatusCode ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service is not reachable at {serviceBaseUrl}: {ex.Message}");
                return 2;
            }
        }
    }

    private static async Task<int> PostMappingAsync(string? id, string action)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.WriteLine($"Usage: vcomtunnelctl {action} <mappingId>");
            return 2;
        }

        if (!TryCreateServiceClient(out var client, out var serviceBaseUrl))
        {
            return 2;
        }

        using (client)
        {
            try
            {
                var response = await client.PostAsync($"/api/mappings/{id}/{action}", null);
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                return response.IsSuccessStatusCode ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service is not reachable at {serviceBaseUrl}: {ex.Message}");
                return 2;
            }
        }
    }

    private static async Task<int> PairAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "create" => await PairPlanAsync($"/api/com0com/mappings/{args.Skip(1).FirstOrDefault()}/create"),
            "create-plan" => await PairPlanAsync($"/api/com0com/mappings/{args.Skip(1).FirstOrDefault()}/create-plan"),
            "remove" => await PairPlanAsync($"/api/com0com/pairs/{args.Skip(1).FirstOrDefault()}/remove"),
            "remove-plan" => await PairPlanAsync($"/api/com0com/pairs/{args.Skip(1).FirstOrDefault()}/remove-plan"),
            _ => PairHelp()
        };
    }

    private static async Task<int> WirelessSerialAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "query" or "scan" => await PairPlanAsync("/api/wireless-serial/endpoints/query"),
            "list" or "endpoints" => await ListWirelessSerialDevicesAsync(),
            _ => WirelessSerialHelp()
        };
    }

    private static async Task<int> ListWirelessSerialDevicesAsync()
    {
        if (!TryCreateServiceClient(out var client, out var serviceBaseUrl))
        {
            return 2;
        }

        using (client)
        {
            try
            {
                var devices = await client.GetFromJsonAsync<List<WirelessSerialDeviceEndpoint>>(
                    "/api/wireless-serial/endpoints",
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));

                if (devices is null || devices.Count == 0)
                {
                    Console.WriteLine("No wireless serial devices discovered.");
                    return 0;
                }

                Console.WriteLine($"{"MAC",-17} {"Endpoint",-22} {"RSSI",5} {"Mode",-10} {"Device",-18} {"Last seen"}");
                foreach (var device in devices.OrderBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
                {
                    var endpoint = device.ServicePort is null
                        ? device.IpAddress
                        : $"{device.IpAddress}:{device.ServicePort}";
                    var name = FirstNonEmpty(device.Alias, device.DeviceId, device.Name, device.Product, device.Board) ?? "-";
                    var rssi = device.WifiRssi?.ToString() ?? "-";
                    var mode = device.Mode ?? "-";
                    Console.WriteLine($"{device.Mac,-17} {endpoint,-22} {rssi,5} {mode,-10} {name,-18} {device.LastSeenAt:O}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service is not reachable at {serviceBaseUrl}: {ex.Message}");
                return 2;
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static async Task<int> PairPlanAsync(string path)
    {
        if (!TryCreateServiceClient(out var client, out var serviceBaseUrl))
        {
            return 2;
        }

        using (client)
        {
            try
            {
                var response = await client.PostAsync(path, null);
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                return response.IsSuccessStatusCode ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service is not reachable at {serviceBaseUrl}: {ex.Message}");
                return 2;
            }
        }
    }

    private static bool TryCreateServiceClient(out HttpClient client, out string serviceBaseUrl)
    {
        try
        {
            serviceBaseUrl = ServiceEndpoint.GetBaseUrl();
            client = new HttpClient { BaseAddress = new Uri(serviceBaseUrl) };
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            serviceBaseUrl = "";
            client = null!;
            Console.WriteLine(ex.Message);
            return false;
        }
    }

    private static int Kmdf(string[] args)
    {
        var options = ParseOptions(args);
        var action = options.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        var manager = new KmdfDeviceManager();
        return action switch
        {
            "list" => ListKmdfPorts(manager),
            "add" => AddKmdfPort(manager, options.Positionals.Skip(1).FirstOrDefault(), options.Positionals.Skip(2).FirstOrDefault(), options.ResultFile),
            "remove" => RemoveKmdfPort(manager, options.Positionals.Skip(1).FirstOrDefault(), options.ResultFile),
            "update" => UpdateKmdfPort(manager, options.Positionals.Skip(1).FirstOrDefault(), options.Positionals.Skip(2).FirstOrDefault(), options.ResultFile),
            "inf" => PrintKmdfInf(options.Positionals.Skip(1).FirstOrDefault()),
            _ => KmdfHelp()
        };
    }

    private static int ListKmdfPorts(KmdfDeviceManager manager)
    {
        try
        {
            var devices = manager.GetDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("No VComTunnel KMDF COM ports found.");
                return 0;
            }

            foreach (var device in devices)
            {
                Console.WriteLine($"{device.PortName,-7} {device.Status,-12} {device.InstanceId} {device.DriverName}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int AddKmdfPort(KmdfDeviceManager manager, string? portName, string? infPath, string? resultFile)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            const string usage = "Usage: vcomtunnelctl kmdf add COM27 [driver-inf]";
            Console.WriteLine(usage);
            WriteKmdfResultFile(resultFile, false, usage);
            return 2;
        }

        var result = manager.AddPort(new KmdfPortRequest(portName, InfPath: infPath));
        return CompleteKmdfResult(result, resultFile);
    }

    private static int RemoveKmdfPort(KmdfDeviceManager manager, string? portName, string? resultFile)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            const string usage = "Usage: vcomtunnelctl kmdf remove COM27";
            Console.WriteLine(usage);
            WriteKmdfResultFile(resultFile, false, usage);
            return 2;
        }

        var result = manager.RemovePort(new KmdfPortRequest(portName));
        return CompleteKmdfResult(result, resultFile);
    }

    private static int UpdateKmdfPort(KmdfDeviceManager manager, string? portName, string? infPath, string? resultFile)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            const string usage = "Usage: vcomtunnelctl kmdf update COM27 [driver-inf]";
            Console.WriteLine(usage);
            WriteKmdfResultFile(resultFile, false, usage);
            return 2;
        }

        var result = manager.UpdatePort(new KmdfPortRequest(portName, InfPath: infPath));
        return CompleteKmdfResult(result, resultFile);
    }

    private static int CompleteKmdfResult(KmdfPortOperationResult result, string? resultFile)
    {
        var text = FormatKmdfResult(result);
        Console.Write(text);
        WriteKmdfResultFile(resultFile, result.Success, text);
        return result.Success ? 0 : 2;
    }

    private static int PrintKmdfInf(string? explicitInfPath)
    {
        var infPath = KmdfDeviceManager.ResolveDriverInfPath(explicitInfPath);
        if (infPath is null)
        {
            Console.WriteLine("VComTunnel.Serial install package was not found.");
            return 2;
        }

        Console.WriteLine(infPath);
        return 0;
    }

    private static void PrintKmdfResult(KmdfPortOperationResult result)
    {
        Console.Write(FormatKmdfResult(result));
    }

    private static string FormatKmdfResult(KmdfPortOperationResult result)
    {
        using var writer = new StringWriter();
        writer.WriteLine($"{(result.Success ? "OK" : "FAIL")} {result.Message}");
        if (result.Device is not null)
        {
            writer.WriteLine($"{result.Device.PortName} {result.Device.Status} {result.Device.InstanceId} {result.Device.DriverName}");
        }

        if (result.RebootRequired)
        {
            writer.WriteLine("A reboot is required to finish this driver operation.");
        }

        return writer.ToString();
    }

    private static void WriteKmdfResultFile(string? resultFile, bool success, string message)
    {
        if (string.IsNullOrWhiteSpace(resultFile))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(resultFile));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var text = message.StartsWith("OK ", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("FAIL ", StringComparison.OrdinalIgnoreCase)
                    ? message
                    : $"{(success ? "OK" : "FAIL")} {message}{Environment.NewLine}";
            File.WriteAllText(resultFile, text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write result file {resultFile}: {ex.Message}");
        }
    }

    private sealed record CliOptions(string[] Positionals, string? ResultFile);

    private static async Task<int> DepsAsync(string[] args)
    {
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        return action switch
        {
            "install" => await InstallDepsAsync(args),
            "launch-com0com" => await LaunchCom0comInstallerAsync(args),
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
        Console.WriteLine("If com0com is prepared but not installed, run: vcomtunnelctl deps launch-com0com");
        return result.Steps.All(s => s.Success) ? 0 : 2;
    }

    private static async Task<int> LaunchCom0comInstallerAsync(string[] args)
    {
        var installer = new DependencyInstaller(new DependencyDetector());
        if (!args.Contains("--wait", StringComparer.OrdinalIgnoreCase))
        {
            if (installer.LaunchCom0comInstaller(out var launchMessage))
            {
                Console.WriteLine(launchMessage);
                return 0;
            }

            Console.WriteLine(launchMessage);
            return 2;
        }

        var installerPath = installer.FindCom0comInstaller();
        if (installerPath is null)
        {
            Console.WriteLine("com0com installer was not found. Run dependency install first.");
            return 2;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null)
            {
                Console.WriteLine("com0com installer could not be started.");
                return 2;
            }

            Console.WriteLine($"Waiting for com0com installer: {installerPath}");
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                Console.WriteLine("com0com installation completed.");
                return 0;
            }
            if (process.ExitCode == 1)
            {
                Console.WriteLine("com0com installation was canceled.");
                return 1;
            }

            Console.WriteLine($"com0com installer failed with exit code {process.ExitCode}.");
            return 2;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.WriteLine("com0com installation was canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"com0com installer could not be started: {ex.Message}");
            return 2;
        }
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

        if (IsServiceInstalled())
        {
            Console.WriteLine("Updating existing Windows service through sc.exe.");
            Console.WriteLine($"Service path: {servicePath}");
            return RunSc("config", ServiceName, "binPath=", servicePath, "start=", "auto", "DisplayName=", "VComTunnel");
        }

        Console.WriteLine("Installing Windows service through sc.exe.");
        Console.WriteLine("The service app can also be run directly for console-mode debugging.");
        return RunSc("create", ServiceName, "binPath=", servicePath, "start=", "auto", "DisplayName=", "VComTunnel");
    }

    private static bool IsServiceInstalled()
    {
        var result = RunScCapture("query", ServiceName);
        return result.ExitCode == 0;
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

    private static ScResult RunScCapture(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ScResult(1, "", "Could not start sc.exe.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScResult(process.ExitCode, output, error);
    }

    private sealed record ScResult(int ExitCode, string Output, string Error);

    private static int Help()
    {
        Console.WriteLine("""
        vcomtunnelctl commands:
          diagnose                 Check com0com, hub4com, optional legacy com2tcp-rfc2217, pnputil
          init-config              Create a sample multi-mapping config
          create-hints             Print setupc.exe commands for com0com pairs
          status                   Read /api/status from the local service
          dependencies             Read /api/dependencies from the local service
          mappings                 Read /api/mappings from the local service
          ports                    List registered com0com pairs
          wireless-serial query    Trigger minimal UDP endpoint discovery
          wireless-serial list     List WirelessSerial endpoints used for MAC-to-COM binding
          pair create <id>         Ask service to create one mapping's com0com pair
          pair create-plan <id>    Print setupc plan for one mapping
          pair remove <n>          Ask service to remove com0com pair number n
          pair remove-plan <n>     Print setupc remove plan for pair number n
          kmdf list                List VComTunnel KMDF COM ports
          kmdf add COMx [inf]      Create an experimental KMDF COM port with administrator approval
          kmdf remove COMx         Remove a KMDF COM port with administrator approval
          kmdf update COMx [inf]   Update an existing experimental KMDF COM port driver
          start <mappingId>        Start one configured mapping
          stop <mappingId>         Stop one configured mapping
          logs                     Read /api/logs from the local service
          deps install [--force] [--no-hub4com] [--no-com0com]
          deps launch-com0com [--wait]
                                  Launch downloaded com0com installer with UAC; optionally wait for completion
          service install [serviceExe] | uninstall | start | stop
        """);
        return 0;
    }

    private static int DepsHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl deps install [--force] [--no-hub4com] [--no-com0com] | launch-com0com [--wait]");
        return 0;
    }

    private static int ServiceHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl service install [serviceExe] | uninstall | start | stop");
        return 0;
    }

    private static int WirelessSerialHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl wireless-serial query | list");
        return 0;
    }

    private static int PairHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl pair create <mappingId> | create-plan <mappingId> | remove <pairNumber> | remove-plan <pairNumber>");
        return 0;
    }

    private static int KmdfHelp()
    {
        Console.WriteLine("Usage: vcomtunnelctl kmdf list | add COM27 [driver-inf] | remove COM27 | update COM27 [driver-inf] | inf [driver-inf]");
        Console.WriteLine(KmdfDeviceManager.TestSignedDriverPolicyHint);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        return Help();
    }
}
