using System.Collections.Concurrent;
using System.Diagnostics;

namespace VComTunnel.Core;

public sealed class TunnelOrchestrator
{
    private readonly ConfigStore _configStore;
    private readonly DependencyDetector _dependencyDetector;
    private readonly Hub4comCommandBuilder _hub4comCommandBuilder;
    private readonly Func<TunnelMapping, Hub4comCommand> _hub4comCommandFactory;
    private readonly IComPortInventory _comPortInventory;
    private readonly InMemoryLog _log;
    private readonly Func<TunnelMapping, InMemoryLog, Action<IKmdfTunnelSession, string>, IKmdfTunnelSession> _kmdfSessionFactory;
    private readonly Func<TunnelMapping, InMemoryLog, Action<IManagedTunnelSession, string>, IManagedTunnelSession> _com0comServiceSessionFactory;
    private readonly TimeSpan _restartDelay;
    private readonly ConcurrentDictionary<string, ManagedTunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastProcessErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _restartVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _intentionalProcessStops = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mappingLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public TunnelOrchestrator(
        ConfigStore configStore,
        DependencyDetector dependencyDetector,
        Hub4comCommandBuilder hub4comCommandBuilder,
        IComPortInventory comPortInventory,
        InMemoryLog log,
        Func<TunnelMapping, InMemoryLog, Action<IKmdfTunnelSession, string>, IKmdfTunnelSession>? kmdfSessionFactory = null,
        Func<TunnelMapping, InMemoryLog, Action<IManagedTunnelSession, string>, IManagedTunnelSession>? com0comServiceSessionFactory = null,
        Func<TunnelMapping, Hub4comCommand>? hub4comCommandFactory = null,
        TimeSpan? restartDelay = null)
    {
        _configStore = configStore;
        _dependencyDetector = dependencyDetector;
        _hub4comCommandBuilder = hub4comCommandBuilder;
        _hub4comCommandFactory = hub4comCommandFactory ?? _hub4comCommandBuilder.Build;
        _comPortInventory = comPortInventory;
        _log = log;
        _kmdfSessionFactory = kmdfSessionFactory ?? ((mapping, sessionLog, faulted) => new KmdfTunnelSession(mapping, sessionLog, faulted));
        _com0comServiceSessionFactory = com0comServiceSessionFactory ?? ((mapping, sessionLog, faulted) => new Com0comServiceTunnelSession(mapping, sessionLog, faulted));
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(2);
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
        var mappingLock = GetMappingLock(id);
        await mappingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mappingLock.Release();
        }
    }

    private async Task<TunnelStatus> StartCoreAsync(string id, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        var mapping = config.Mappings.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mapping '{id}' was not found.");

        if (mapping.Backend == TunnelBackend.Kmdf)
        {
            StopEndpointConflicts(mapping);
            return await StartKmdfAsync(mapping, cancellationToken);
        }

        if (mapping.Backend == TunnelBackend.Com0comService)
        {
            return await StartCom0comServiceAsync(mapping, cancellationToken);
        }

        var dependencies = _dependencyDetector.Detect();
        foreach (var item in dependencies.Items)
        {
            _log.Info("dependencies", $"{item.Name}: {(item.Found ? item.Path : item.Message)}");
        }

        if (!dependencies.IsReadyForCom0comHub4com)
        {
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, "com0com/hub4com dependencies are missing.");
            _tunnels[id] = faulted;
            _log.Error(mapping.Name, faulted.LastError!);
            return faulted.ToStatus();
        }

        if (!IsBackingPortRegistered(mapping, out var portError))
        {
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, portError);
            _tunnels[id] = faulted;
            _log.Error(mapping.Name, faulted.LastError!);
            return faulted.ToStatus();
        }

        StopEndpointConflicts(mapping);
        StopExisting(id);
        _lastProcessErrors.TryRemove(id, out _);
        var command = _hub4comCommandFactory(mapping);
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

        _log.Info(
            mapping.Name,
            mapping.Hub4comForwardControlLines
                ? "Started hub4com bridge with control-line forwarding enabled; DTR/RTS/BREAK and line-control changes may reach the target."
                : "Started no-control-lines hub4com bridge; DTR/RTS/BREAK are not forwarded by default.");

        var tunnel = new ManagedTunnel(mapping, TunnelRunState.Running, process, null, DateTimeOffset.UtcNow, null);
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
        var mappingLock = GetMappingLock(id);
        mappingLock.Wait();
        try
        {
            var mapping = _tunnels.TryGetValue(id, out var existing)
                ? existing.Mapping
                : new TunnelMapping { Id = id };
            StopExisting(id);
            _lastProcessErrors.TryRemove(id, out _);
            var stopped = new ManagedTunnel(mapping, TunnelRunState.Stopped, null, null, null, null);
            _tunnels[id] = stopped;
            _log.Info(mapping.Name, "Stopped.");
            return stopped.ToStatus();
        }
        finally
        {
            mappingLock.Release();
        }
    }

    public ServiceStatus GetStatus()
    {
        return new ServiceStatus(
            _startedAt,
            _configStore.Path,
            _tunnels.Values.Select(t => t.ToStatus()).OrderBy(t => t.Id).ToArray());
    }

    private async Task<TunnelStatus> StartKmdfAsync(
        TunnelMapping mapping,
        CancellationToken cancellationToken)
    {
        StopExisting(mapping.Id);

        var effectiveMapping = mapping with { SuppressInitialControlLineSync = true };
        var session = _kmdfSessionFactory(effectiveMapping, _log, (faultedSession, error) => OnKmdfFaulted(mapping, faultedSession, error));
        return await StartManagedSessionAsync(mapping, session, cancellationToken);
    }

    private async Task<TunnelStatus> StartCom0comServiceAsync(
        TunnelMapping mapping,
        CancellationToken cancellationToken)
    {
        if (!IsBackingPortRegistered(mapping, out var portError))
        {
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, portError);
            _tunnels[mapping.Id] = faulted;
            _log.Error(mapping.Name, faulted.LastError!);
            return faulted.ToStatus();
        }

        StopEndpointConflicts(mapping);
        StopExisting(mapping.Id);

        var session = _com0comServiceSessionFactory(mapping, _log, (faultedSession, error) => OnSessionFaulted(mapping, faultedSession, error));
        return await StartManagedSessionAsync(mapping, session, cancellationToken);
    }

    private async Task<TunnelStatus> StartManagedSessionAsync(
        TunnelMapping mapping,
        IManagedTunnelSession session,
        CancellationToken cancellationToken)
    {
        _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Starting, null, session, DateTimeOffset.UtcNow, null);

        try
        {
            await session.StartAsync(cancellationToken);
            var running = new ManagedTunnel(mapping, TunnelRunState.Running, null, session, DateTimeOffset.UtcNow, null);
            _tunnels[mapping.Id] = running;
            return running.ToStatus();
        }
        catch (Exception ex)
        {
            session.Dispose();
            var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, ex.Message);
            _tunnels[mapping.Id] = faulted;
            _log.Error(mapping.Name, ex.Message);
            ScheduleSessionRestart(mapping, ex.Message);
            return faulted.ToStatus();
        }
    }

    private void OnKmdfFaulted(TunnelMapping mapping, IKmdfTunnelSession session, string error)
    {
        OnSessionFaulted(mapping, session, error);
    }

    private void OnSessionFaulted(TunnelMapping mapping, IManagedTunnelSession session, string error)
    {
        if (!_tunnels.TryGetValue(mapping.Id, out var existing) || !ReferenceEquals(existing.Session, session))
        {
            _log.Info(mapping.Name, $"Ignored stale {FormatBackendForLog(mapping.Backend)} fault after the tunnel was stopped or replaced: {error}");
            return;
        }

        _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, error);
        ScheduleSessionRestart(mapping, error);
    }

    private void ScheduleSessionRestart(TunnelMapping mapping, string error)
    {
        if (!mapping.RestartOnFailure || IsPermanentSessionError(error))
        {
            return;
        }

        var restartVersion = GetRestartVersion(mapping.Id);
        var backendName = FormatBackendForLog(mapping.Backend);
        _log.Warn(mapping.Name, $"Scheduling {backendName} restart in {_restartDelay.TotalMilliseconds:0} ms after: {error}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(_restartDelay);
            if (GetRestartVersion(mapping.Id) != restartVersion)
            {
                return;
            }

            try
            {
                await StartAsync(mapping.Id);
            }
            catch (Exception ex)
            {
                _log.Error(mapping.Name, $"{backendName} restart failed: {ex.Message}");
            }
        });
    }

    private void StopExisting(string id)
    {
        BumpRestartVersion(id);
        StopProcess(id);
        StopSession(id);
    }

    private void StopEndpointConflicts(TunnelMapping mapping)
    {
        var conflicts = _tunnels.Values
            .Where(existing => !string.Equals(existing.Mapping.Id, mapping.Id, StringComparison.OrdinalIgnoreCase))
            .Where(existing => UsesSameEndpoint(existing.Mapping, mapping))
            .Where(existing => existing.ToStatus().State is not TunnelRunState.Stopped)
            .ToArray();

        foreach (var conflict in conflicts)
        {
            var endpoint = FormatEndpoint(mapping);
            _log.Info(
                mapping.Name,
                $"Stopping {conflict.Mapping.Name} ({conflict.Mapping.VisiblePort}) before starting because both use RFC2217 endpoint {endpoint}.");
            StopExisting(conflict.Mapping.Id);
            _lastProcessErrors.TryRemove(conflict.Mapping.Id, out _);
            _tunnels[conflict.Mapping.Id] = new ManagedTunnel(conflict.Mapping, TunnelRunState.Stopped, null, null, null, null);
            _log.Info(conflict.Mapping.Name, $"Stopped because {mapping.Name} is starting for the same RFC2217 endpoint {endpoint}.");
        }
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
                _intentionalProcessStops.TryAdd(existing.Process.Id, 0);
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

    private void StopSession(string id)
    {
        if (!_tunnels.TryGetValue(id, out var existing) || existing.Session is null)
        {
            return;
        }

        existing.Session.Dispose();
    }

    private void OnProcessExited(TunnelMapping mapping, Process process)
    {
        if (_intentionalProcessStops.TryRemove(process.Id, out _))
        {
            _log.Info(mapping.Name, $"Stopped hub4com process {process.Id}.");
            return;
        }

        var message = BuildExitMessage(mapping, process);
        _log.Warn(mapping.Name, message);
        _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, message);

        if (!mapping.RestartOnFailure || IsPermanentProcessError(message))
        {
            return;
        }

        var restartVersion = GetRestartVersion(mapping.Id);
        _ = Task.Run(async () =>
        {
            await Task.Delay(_restartDelay);
            if (GetRestartVersion(mapping.Id) != restartVersion)
            {
                return;
            }

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
        var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, message);
        _tunnels[mapping.Id] = faulted;
        return faulted.ToStatus();
    }

    private string BuildExitMessage(TunnelMapping mapping, Process process)
    {
        var detail = _lastProcessErrors.TryGetValue(mapping.Id, out var lastError) ? $" Last error: {lastError}" : "";
        if (IsMissingBackingPortError(detail) && !string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            detail += $" Create the com0com pair first: setupc.exe install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}";
        }
        else if (IsBackingPortAccessDeniedError(detail) && !string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            detail += $" Backing port {mapping.BackingPort} is already open or access was denied. Stop the VComTunnel mapping, hub4com/com2tcp, or serial tool using {mapping.BackingPort}, then retry.";
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
        return IsMissingBackingPortError(value) || IsBackingPortAccessDeniedError(value);
    }

    private static bool IsMissingBackingPortError(string value)
    {
        return value.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            && value.Contains("ERROR 2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackingPortAccessDeniedError(string value)
    {
        return value.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            && (value.Contains("ERROR 5", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Access is denied", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPermanentSessionError(string value)
    {
        return value.Contains("control channel", StringComparison.OrdinalIgnoreCase)
            || value.Contains("driver protocol", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ack returned unexpected value", StringComparison.OrdinalIgnoreCase)
            || value.Contains("only available on Windows", StringComparison.OrdinalIgnoreCase)
            || value.Contains("backingPort is required", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBackendForLog(TunnelBackend backend)
    {
        return backend switch
        {
            TunnelBackend.Kmdf => "KMDF",
            TunnelBackend.Com0comService => "com0comService",
            _ => backend.ToString()
        };
    }

    private static bool UsesSameEndpoint(TunnelMapping left, TunnelMapping right)
    {
        return left.Protocol == right.Protocol
            && left.Port == right.Port
            && string.Equals(left.Host.Trim(), right.Host.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEndpoint(TunnelMapping mapping)
    {
        return $"{mapping.Host.Trim()}:{mapping.Port}";
    }

    private long GetRestartVersion(string id)
    {
        return _restartVersions.GetOrAdd(id, 0);
    }

    private SemaphoreSlim GetMappingLock(string id)
    {
        return _mappingLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    }

    private void BumpRestartVersion(string id)
    {
        _restartVersions.AddOrUpdate(id, 1, (_, version) => version + 1);
    }

    private bool IsBackingPortRegistered(TunnelMapping mapping, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            error = "backingPort is required for com0com mappings.";
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
        IManagedTunnelSession? Session,
        DateTimeOffset? StartedAt,
        string? LastError)
    {
        public TunnelStatus ToStatus()
        {
            var state = Session?.State ?? State;
            var lastError = Session?.LastError ?? LastError;
            return new TunnelStatus(
                Mapping.Id,
                state,
                Mapping.Backend.ToString(),
                Process?.HasExited == false ? Process.Id : null,
                StartedAt,
                lastError);
        }
    }
}
