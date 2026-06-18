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

        builder.WebHost.UseUrls("http://127.0.0.1:44817");
        builder.Services.AddSingleton<ConfigStore>();
        builder.Services.AddSingleton<DependencyDetector>();
        builder.Services.AddSingleton<DependencyInstaller>();
        builder.Services.AddSingleton<Hub4comCommandBuilder>();
        builder.Services.AddSingleton<IComPortInventory, WindowsComPortInventory>();
        builder.Services.AddSingleton<Com0comSetupManager>();
        builder.Services.AddSingleton<KmdfDeviceManager>();
        builder.Services.AddSingleton<InMemoryLog>();
        builder.Services.AddSingleton<TunnelOrchestrator>();
        builder.Services.AddHostedService<AutoStartHostedService>();
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
                    logsDirectory = AppPaths.LogsDirectory
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

        app.MapPut("/api/mappings", async (
            List<TunnelMapping> mappings,
            TunnelOrchestrator tunnels,
            CancellationToken requestToken) =>
        {
            var errors = await tunnels.SaveConfigAsync(new VComTunnelConfig { Mappings = mappings }, requestToken);
            return errors.Count == 0
                ? Results.Ok(new { saved = mappings.Count })
                : Results.BadRequest(new { errors });
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
    private readonly InMemoryLog _log;

    public AutoStartHostedService(TunnelOrchestrator tunnels, InMemoryLog log)
    {
        _tunnels = tunnels;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await _tunnels.StartAutoStartMappingsAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("service", $"AutoStart failed: {ex.Message}");
        }
    }
}
