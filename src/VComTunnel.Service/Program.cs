using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using VComTunnel.Core;

if (ShouldRunAsWindowsService(args))
{
    ServiceBase.Run(new VComTunnelWindowsService(args));
}
else
{
    await VComTunnelHost.RunAsync(args, CancellationToken.None);
}

static bool ShouldRunAsWindowsService(string[] args)
{
    return OperatingSystem.IsWindows()
        && !Environment.UserInteractive
        && !args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
}

internal static class VComTunnelHost
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.UseUrls(ServiceEndpoint.GetBaseUrl());
        builder.Services.AddSingleton<ConfigStore>();
        builder.Services.AddSingleton<DependencyDetector>();
        builder.Services.AddSingleton<DependencyInstaller>();
        builder.Services.AddSingleton<Hub4comCommandBuilder>();
        builder.Services.AddSingleton<IComPortInventory, WindowsComPortInventory>();
        builder.Services.AddSingleton<Com0comSetupManager>();
        builder.Services.AddSingleton<KmdfDeviceManager>();
        builder.Services.AddSingleton<InMemoryLog>();
        builder.Services.AddSingleton<WirelessSerialEndpointRegistry>(services =>
        {
            var log = services.GetRequiredService<InMemoryLog>();
            var store = services.GetRequiredService<ConfigStore>();
            return new WirelessSerialEndpointRegistry(
                log,
                periodicQueryEnabled: token => HasWirelessSerialMacBoundMappingAsync(store, token));
        });
        builder.Services.AddSingleton<TunnelOrchestrator>();
        builder.Services.AddSingleton<SerialBackgroundLogManager>();
        builder.Services.AddHostedService<AutoStartHostedService>();
        builder.Services.AddHostedService<WirelessSerialEndpointHostedService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();

        app.MapGet("/", () => Results.Redirect("/api/status"));

        app.MapGet("/api/status", (TunnelOrchestrator tunnels) =>
        {
            return Results.Ok(tunnels.GetStatus());
        });

        app.MapGet("/api/dependencies", (DependencyDetector dependencies) =>
        {
            return Results.Ok(dependencies.Detect());
        });

        app.MapGet("/api/debug/paths", (DependencyDetector dependencies) =>
        {
            return Results.Ok(new
            {
                appPaths = new
                {
                    programDataRoot = AppPaths.ProgramDataRoot,
                    toolsDirectory = AppPaths.ToolsDirectory,
                    configPath = AppPaths.ConfigPath,
                    logsDirectory = AppPaths.LogsDirectory,
                    serialTrafficLogsDirectory = AppPaths.SerialTrafficLogsDirectory
                },
                environment = new
                {
                    currentDirectory = Environment.CurrentDirectory,
                    baseDirectory = AppContext.BaseDirectory,
                    vcomTunnelHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME")
                },
                candidateRoots = dependencies.CandidateRoots.Select(root => new
                {
                    path = root,
                    exists = Directory.Exists(root)
                }).ToArray()
            });
        });

        app.MapPost("/api/dependencies/install", async (
            DependencyInstallRequest? request,
            DependencyInstaller installer,
            InMemoryLog log,
            CancellationToken requestToken) =>
        {
            log.Info("dependencies", "Dependency install requested.");
            var result = await installer.InstallAsync(request ?? new DependencyInstallRequest(), requestToken);
            foreach (var step in result.Steps)
            {
                var message = $"{step.Name}: {step.Message}" + (step.Path is null ? "" : $" ({step.Path})");
                if (step.Success)
                {
                    log.Info("dependencies", message);
                }
                else
                {
                    log.Error("dependencies", message);
                }
            }

            log.Info("dependencies", $"com0com/hub4com ready: {result.DependencyReport.IsReadyForCom0comHub4com}");
            return Results.Ok(result);
        });

        app.MapGet("/api/mappings", async (TunnelOrchestrator tunnels, CancellationToken requestToken) =>
        {
            var config = await tunnels.GetConfigAsync(requestToken);
            return Results.Ok(config.Mappings);
        });

        app.MapGet("/api/traffic-logs", async (
            TunnelOrchestrator tunnels,
            SerialBackgroundLogManager backgroundLogs,
            CancellationToken requestToken) =>
        {
            var config = await tunnels.GetConfigAsync(requestToken);
            var statuses = tunnels.GetStatus().Tunnels.ToDictionary(status => status.Id, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(config.Mappings.Select(mapping =>
                backgroundLogs.GetStatus(mapping, statuses.GetValueOrDefault(mapping.Id))));
        });

        app.MapPut("/api/mappings", async (
            List<TunnelMapping> mappings,
            TunnelOrchestrator tunnels,
            SerialBackgroundLogManager backgroundLogs,
            CancellationToken requestToken) =>
        {
            var errors = await tunnels.SaveConfigAsync(new VComTunnelConfig { Mappings = mappings }, requestToken);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            await backgroundLogs.SyncAsync(mappings, requestToken);
            return Results.Ok(new { saved = mappings.Count });
        });

        app.MapPost("/api/mappings/{id}/start", async (
            string id,
            TunnelOrchestrator tunnels,
            CancellationToken requestToken) =>
        {
            try
            {
                return Results.Ok(await tunnels.StartAsync(id, requestToken));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/mappings/{id}/stop", (string id, TunnelOrchestrator tunnels) =>
        {
            return Results.Ok(tunnels.Stop(id));
        });

        app.MapGet("/api/logs", (InMemoryLog log, int? max) =>
        {
            return Results.Ok(log.Snapshot(max.GetValueOrDefault(500)));
        });

        app.MapDelete("/api/logs", (InMemoryLog log) =>
        {
            log.Clear();
            return Results.Ok(new { cleared = true });
        });

        app.MapGet("/api/com0com/pairs", (Com0comSetupManager setup) =>
        {
            return Results.Ok(setup.GetPairs());
        });

        app.MapGet("/api/wireless-serial/endpoints", (WirelessSerialEndpointRegistry endpoints) =>
        {
            return Results.Ok(endpoints.Snapshot());
        });

        app.MapPost("/api/wireless-serial/endpoints/query", async (
            WirelessSerialEndpointRegistry endpoints,
            CancellationToken requestToken) =>
        {
            return Results.Ok(await endpoints.SendQueryAsync(requestToken));
        });

        app.MapPost("/api/wireless-serial/endpoints", (
            WirelessSerialEndpointUpdateRequest request,
            WirelessSerialEndpointRegistry endpoints,
            InMemoryLog log) =>
        {
            try
            {
                return Results.Ok(endpoints.Upsert(request));
            }
            catch (ArgumentException ex)
            {
                log.Warn("wireless-serial", ex.Message);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/com0com/mappings/{id}/create-plan", async (
            string id,
            Com0comSetupManager setup,
            CancellationToken requestToken) =>
        {
            try
            {
                return Results.Ok(await setup.BuildCreatePlanAsync(id, requestToken));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/com0com/mappings/{id}/create", async (
            string id,
            Com0comSetupManager setup,
            CancellationToken requestToken) =>
        {
            try
            {
                var result = await setup.CreatePairAsync(id, requestToken);
                return result.Ok
                    ? Results.Ok(result)
                    : Results.BadRequest(result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/com0com/pairs/{pairNumber:int}/remove-plan", (
            int pairNumber,
            Com0comSetupManager setup) =>
        {
            try
            {
                return Results.Ok(setup.BuildRemovePlan(pairNumber));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/com0com/pairs/{pairNumber:int}/remove", async (
            int pairNumber,
            Com0comSetupManager setup,
            CancellationToken requestToken) =>
        {
            try
            {
                var result = await setup.RemovePairAsync(pairNumber, requestToken);
                return result.Ok
                    ? Results.Ok(result)
                    : Results.BadRequest(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/kmdf/devices", (KmdfDeviceManager devices) =>
        {
            try
            {
                return Results.Ok(devices.GetDevices());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/kmdf/ports/add", (
            KmdfPortRequest request,
            KmdfDeviceManager devices,
            InMemoryLog log) =>
        {
            var result = devices.AddPort(request);
            if (result.Success)
            {
                log.Info("kmdf", result.Message);
                return Results.Ok(result);
            }

            log.Error("kmdf", result.Message);
            return Results.BadRequest(result);
        });

        app.MapPost("/api/kmdf/ports/remove", (
            KmdfPortRequest request,
            KmdfDeviceManager devices,
            InMemoryLog log) =>
        {
            var result = devices.RemovePort(request);
            if (result.Success)
            {
                log.Info("kmdf", result.Message);
                return Results.Ok(result);
            }

            log.Error("kmdf", result.Message);
            return Results.BadRequest(result);
        });

        app.MapPost("/api/kmdf/ports/update", (
            KmdfPortRequest request,
            KmdfDeviceManager devices,
            InMemoryLog log) =>
        {
            var result = devices.UpdatePort(request);
            if (result.Success)
            {
                log.Info("kmdf", result.Message);
                return Results.Ok(result);
            }

            log.Error("kmdf", result.Message);
            return Results.BadRequest(result);
        });

        await app.RunAsync(cancellationToken);
    }

    private static async Task<bool> HasWirelessSerialMacBoundMappingAsync(ConfigStore store, CancellationToken cancellationToken)
    {
        var config = await store.LoadAsync(cancellationToken);
        return WirelessSerialEndpointRegistry.HasMacBoundMapping(config.Mappings);
    }
}

internal sealed class VComTunnelWindowsService : ServiceBase
{
    private readonly string[] _args;
    private CancellationTokenSource? _cts;
    private Task? _hostTask;

    public VComTunnelWindowsService(string[] args)
    {
        _args = args;
        ServiceName = "VComTunnel";
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _hostTask = Task.Run(() => VComTunnelHost.RunAsync(_args, _cts.Token));
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try
        {
            _hostTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _cts?.Dispose();
        }
    }
}

internal sealed class AutoStartHostedService : BackgroundService
{
    private readonly TunnelOrchestrator _tunnels;
    private readonly SerialBackgroundLogManager _backgroundLogs;
    private readonly InMemoryLog _log;

    public AutoStartHostedService(
        TunnelOrchestrator tunnels,
        SerialBackgroundLogManager backgroundLogs,
        InMemoryLog log)
    {
        _tunnels = tunnels;
        _backgroundLogs = backgroundLogs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            try
            {
                await _tunnels.StartAutoStartMappingsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.Error("service", $"AutoStart mapping startup failed: {ex.Message}");
            }

            try
            {
                var config = await _tunnels.GetConfigAsync(stoppingToken);
                await _backgroundLogs.SyncAsync(config.Mappings, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.Error("service", $"Background logging restore failed: {ex.Message}");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _backgroundLogs.DisposeAsync();
        }
    }
}

internal sealed class WirelessSerialEndpointHostedService : BackgroundService
{
    private readonly WirelessSerialEndpointRegistry _endpoints;
    private readonly InMemoryLog _log;

    public WirelessSerialEndpointHostedService(WirelessSerialEndpointRegistry endpoints, InMemoryLog log)
    {
        _endpoints = endpoints;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _endpoints.StartAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("wireless-serial", $"Endpoint discovery service failed: {ex.Message}");
        }
        finally
        {
            await _endpoints.StopAsync();
        }
    }
}
