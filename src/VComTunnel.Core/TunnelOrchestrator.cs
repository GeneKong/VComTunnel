using System.Collections.Concurrent;
using System.Diagnostics;

namespace VComTunnel.Core;

public sealed class TunnelOrchestrator
{
    private readonly ConfigStore _configStore;
    private readonly DependencyDetector _dependencyDetector;
    private readonly Hub4comCommandBuilder _hub4comCommandBuilder;
    private readonly IComPortInventory _comPortInventory;
    private readonly InMemoryLog _log;
    private readonly ConcurrentDictionary<string, ManagedTunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastProcessErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public TunnelOrchestrator(
        ConfigStore configStore,
        DependencyDetector dependencyDetector,
        Hub4comCommandBuilder hub4comCommandBuilder,
        IComPortInventory comPortInventory,
        InMemoryLog log)
    {
        _configStore = configStore;
        _dependencyDetector = dependencyDetector;
        _hub4comCommandBuilder = hub4comCommandBuilder;
        _comPortInventory = comPortInventory;
        _log = log;
    }

    public async Task<VComTunnelConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        return await _configStore.LoadAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SaveConfigAsync(VComTunnelConfig config, CancellationToken cancellationToken = default)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            return errors;
        }

        await _configStore.SaveAsync(config, cancellationToken);
        _log.Info("config", $"Saved {config.Mappings.Count} mapping(s).");
        return [];
    }

    public async Task StartAutoStartMappingsAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        foreach (var mapping in config.Mappings.Where(m => m.AutoStart))
        {
            await StartAsync(mapping.Id, cancellationToken);
        }
    }

    public async Task<TunnelStatus> StartAsync(string id, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        var mapping = config.Mappings.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mapping '{id}' was not found.");

        if (mapping.Backend == TunnelBackend.Kmdf)
        {
            var unsupported = new ManagedTunnel(mapping, TunnelRunState.Unsupported, null, null, "KMDF backend scaffold is present but driver/service channel is not implemented yet.");
            _tunnels[id] = unsupported;
            _log.Warn(mapping.Name, unsupported.LastError!);
            return unsupported.ToStatus();
        }

        var dependencies = _dependencyDetector.Detect();
        foreach (var item in dependencies.Items)
        {
            _log.Info("dependencies", $"{item.Name}: {(item.Found ? item.Path : item.Message)}");
        }

        if (!dependencies.IsReadyForCom0comHub4com)
        {
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, "com0com/hub4com dependencies are missing.");
            _tunnels[id] = faulted;
            _log.Error(mapping.Name, faulted.LastError!);
            return faulted.ToStatus();
        }

        if (!IsBackingPortRegistered(mapping, out var portError))
        {
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, portError);
            _tunnels[id] = faulted;
            _log.Error(mapping.Name, faulted.LastError!);
            return faulted.ToStatus();
        }

        StopProcess(id);
        _lastProcessErrors.TryRemove(id, out _);
        var command = _hub4comCommandBuilder.Build(mapping);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Info(mapping.Name, e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            _log.Warn(mapping.Name, e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastProcessErrors.AddOrUpdate(
                    mapping.Id,
                    e.Data,
                    (_, existing) => IsRootCauseError(existing) ? existing : e.Data);
            }
        };
        process.Exited += (_, _) => OnProcessExited(mapping, process);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var tunnel = new ManagedTunnel(mapping, TunnelRunState.Running, process, DateTimeOffset.UtcNow, null);
        _tunnels[id] = tunnel;
        _log.Info(mapping.Name, $"Started hub4com process {process.Id}.");

        await Task.Delay(500, cancellationToken);
        if (process.HasExited)
        {
            return BuildExitedStatus(mapping, process);
        }

        return tunnel.ToStatus();
    }

    public TunnelStatus Stop(string id)
    {
        StopProcess(id);
        _lastProcessErrors.TryRemove(id, out _);
        var stopped = new ManagedTunnel(new TunnelMapping { Id = id }, TunnelRunState.Stopped, null, null, null);
        _tunnels[id] = stopped;
        _log.Info(id, "Stopped.");
        return stopped.ToStatus();
    }

    public ServiceStatus GetStatus()
    {
        return new ServiceStatus(
            _startedAt,
            _configStore.Path,
            _tunnels.Values.Select(t => t.ToStatus()).OrderBy(t => t.Id).ToArray());
    }

    private void StopProcess(string id)
    {
        if (!_tunnels.TryGetValue(id, out var existing) || existing.Process is null)
        {
            return;
        }

        try
        {
            if (!existing.Process.HasExited)
            {
                existing.Process.Kill(entireProcessTree: true);
                existing.Process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(existing.Mapping.Name, $"Failed to stop process: {ex.Message}");
        }
        finally
        {
            existing.Process.Dispose();
        }
    }

    private void OnProcessExited(TunnelMapping mapping, Process process)
    {
        var message = BuildExitMessage(mapping, process);
        _log.Warn(mapping.Name, message);
        _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, message);

        if (!mapping.RestartOnFailure || IsPermanentProcessError(message))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                await StartAsync(mapping.Id);
            }
            catch (Exception ex)
            {
                _log.Error(mapping.Name, $"Restart failed: {ex.Message}");
            }
        });
    }

    private TunnelStatus BuildExitedStatus(TunnelMapping mapping, Process process)
    {
        var message = BuildExitMessage(mapping, process);
        var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, message);
        _tunnels[mapping.Id] = faulted;
        return faulted.ToStatus();
    }

    private string BuildExitMessage(TunnelMapping mapping, Process process)
    {
        var detail = _lastProcessErrors.TryGetValue(mapping.Id, out var lastError) ? $" Last error: {lastError}" : "";
        if (detail.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            && detail.Contains("ERROR 2", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            detail += $" Create the com0com pair first: setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}";
        }

        return $"hub4com exited with code {process.ExitCode}.{detail}";
    }

    private static bool IsRootCauseError(string value)
    {
        return value.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ERROR 2", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermanentProcessError(string value)
    {
        return value.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            && value.Contains("ERROR 2", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsBackingPortRegistered(TunnelMapping mapping, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            error = "backingPort is required for com0com/hub4com mappings.";
            return false;
        }

        var ports = _comPortInventory.GetRegisteredPortNames();
        if (ports.Count == 0)
        {
            return true;
        }

        if (ports.Any(port => string.Equals(port, mapping.BackingPort, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        error = $"Backing port {mapping.BackingPort} is not registered in the Windows COM database. Existing ports: {string.Join(", ", ports)}. Choose the other side of an existing com0com pair, or create it first: setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}";
        return false;
    }

    private sealed record ManagedTunnel(
        TunnelMapping Mapping,
        TunnelRunState State,
        Process? Process,
        DateTimeOffset? StartedAt,
        string? LastError)
    {
        public TunnelStatus ToStatus()
        {
            return new TunnelStatus(
                Mapping.Id,
                State,
                Mapping.Backend.ToString(),
                Process?.HasExited == false ? Process.Id : null,
                StartedAt,
                LastError);
        }
    }
}
