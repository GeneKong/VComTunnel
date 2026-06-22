using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace VComTunnel.Core;

public sealed class TunnelOrchestrator
{
    private const int RestartScheduleLogAttempts = 2;

    private readonly ConfigStore _configStore;
    private readonly DependencyDetector _dependencyDetector;
    private readonly Hub4comCommandBuilder _hub4comCommandBuilder;
    private readonly Func<TunnelMapping, Hub4comCommand> _hub4comCommandFactory;
    private readonly IComPortInventory _comPortInventory;
    private readonly InMemoryLog _log;
    private readonly Func<TunnelMapping, InMemoryLog, Action<IKmdfTunnelSession, string>, IKmdfTunnelSession> _kmdfSessionFactory;
    private readonly Func<TunnelMapping, InMemoryLog, Action<IManagedTunnelSession, string>, IManagedTunnelSession> _com0comServiceSessionFactory;
    private readonly TimeSpan _restartInitialDelay;
    private readonly TimeSpan _restartMaxDelay;
    private readonly TimeSpan _portReleaseRetryTimeout;
    private readonly TimeSpan _portReleaseRetryDelay;
    private readonly TimeSpan _sessionStartTimeout;
    private readonly ConcurrentDictionary<string, ManagedTunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastProcessErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _restartVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RestartBackoffState> _restartBackoffs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastRunningByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _intentionalProcessStops = new();
    private readonly object _runtimeStateLock = new();
    private readonly string _runtimeStatePath;
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
        TimeSpan? restartDelay = null,
        TimeSpan? portReleaseRetryTimeout = null,
        TimeSpan? portReleaseRetryDelay = null,
        TimeSpan? restartMaxDelay = null,
        TimeSpan? sessionStartTimeout = null)
    {
        _configStore = configStore;
        _dependencyDetector = dependencyDetector;
        _hub4comCommandBuilder = hub4comCommandBuilder;
        _hub4comCommandFactory = hub4comCommandFactory ?? _hub4comCommandBuilder.Build;
        _comPortInventory = comPortInventory;
        _log = log;
        _kmdfSessionFactory = kmdfSessionFactory ?? ((mapping, sessionLog, faulted) => new KmdfTunnelSession(mapping, sessionLog, faulted));
        _com0comServiceSessionFactory = com0comServiceSessionFactory ?? ((mapping, sessionLog, faulted) => new Com0comServiceTunnelSession(mapping, sessionLog, faulted));
        var configDirectory = Path.GetDirectoryName(_configStore.Path);
        _runtimeStatePath = Path.Combine(
            string.IsNullOrWhiteSpace(configDirectory) ? AppPaths.ProgramDataRoot : configDirectory,
            "runtime-state.json");
        LoadRuntimeState();
        _restartInitialDelay = restartDelay ?? TimeSpan.FromSeconds(2);
        _restartMaxDelay = restartMaxDelay ?? TimeSpan.FromMinutes(2);
        if (_restartInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(restartDelay), "Restart delay must be greater than zero.");
        }

        if (_restartMaxDelay < _restartInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(restartMaxDelay), "Restart max delay must be greater than or equal to the initial restart delay.");
        }

        _portReleaseRetryTimeout = portReleaseRetryTimeout ?? TimeSpan.FromSeconds(3);
        _portReleaseRetryDelay = portReleaseRetryDelay ?? TimeSpan.FromMilliseconds(150);
        _sessionStartTimeout = sessionStartTimeout ?? TimeSpan.FromSeconds(15);
        if (_sessionStartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionStartTimeout), "Session start timeout must be greater than zero.");
        }
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
        ApplySavedConfigToKnownTunnels(config.Mappings);
        _log.Info("config", $"Saved {config.Mappings.Count} mapping(s).");
        return [];
    }

    private void ApplySavedConfigToKnownTunnels(IReadOnlyList<TunnelMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (!_tunnels.TryGetValue(mapping.Id, out var existing))
            {
                continue;
            }

            var previous = existing.Mapping;
            var status = existing.ToStatus();
            existing.Session?.UpdateMapping(mapping);
            _tunnels[mapping.Id] = existing with { Mapping = mapping };
            ApplyRestartOptionChange(previous, mapping, status);

            if (status.State is TunnelRunState.Stopped)
            {
                continue;
            }

            LogRuntimeOptionChanges(previous, mapping, existing.Session is not null);
        }
    }

    private void ApplyRestartOptionChange(TunnelMapping previous, TunnelMapping current, TunnelStatus previousStatus)
    {
        if (previous.RestartOnFailure == current.RestartOnFailure)
        {
            return;
        }

        if (!current.RestartOnFailure)
        {
            BumpRestartVersion(current.Id);
            ResetRestartBackoff(current.Id);
            return;
        }

        if (previousStatus.State is not TunnelRunState.Faulted || string.IsNullOrWhiteSpace(previousStatus.LastError))
        {
            return;
        }

        if (IsPermanentRestartError(current.Backend, previousStatus.LastError))
        {
            return;
        }

        ScheduleRestart(current, previousStatus.LastError, FormatBackendForLog(current.Backend));
    }

    private static bool IsPermanentRestartError(TunnelBackend backend, string error)
    {
        return backend == TunnelBackend.Com0comHub4com
            ? IsPermanentProcessError(error)
            : IsPermanentSessionError(error);
    }

    private void LogRuntimeOptionChanges(TunnelMapping previous, TunnelMapping current, bool hasManagedSession)
    {
        if (previous.Hub4comForwardControlLines != current.Hub4comForwardControlLines)
        {
            if (hasManagedSession)
            {
                _log.Info(current.Name, $"Runtime control-line forwarding {(current.Hub4comForwardControlLines ? "enabled" : "disabled")} for active {FormatBackendForLog(current.Backend)} tunnel.");
            }
            else
            {
                _log.Info(current.Name, "Control-line forwarding change saved; restart this hub4com tunnel for it to affect the running process.");
            }
        }

        if (previous.AutoStart != current.AutoStart)
        {
            _log.Info(current.Name, $"Runtime auto-start option {(current.AutoStart ? "enabled" : "disabled")}.");
        }

        if (previous.RestartOnFailure != current.RestartOnFailure)
        {
            _log.Info(current.Name, $"Runtime restart-on-failure option {(current.RestartOnFailure ? "enabled" : "disabled")}.");
        }
    }

    public async Task StartAutoStartMappingsAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var autoStartMappings = SelectAutoStartMappings(config.Mappings.Where(m => m.AutoStart).ToArray());
        var starts = autoStartMappings
            .Select(mapping => StartAutoStartMappingAsync(mapping, cancellationToken))
            .ToArray();

        await Task.WhenAll(starts).ConfigureAwait(false);
    }

    private async Task StartAutoStartMappingAsync(TunnelMapping mapping, CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(mapping.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error(mapping.Name, $"AutoStart failed: {ex.Message}");
        }
    }

    private IReadOnlyList<TunnelMapping> SelectAutoStartMappings(IReadOnlyList<TunnelMapping> mappings)
    {
        var endpointGroups = mappings
            .Select((Mapping, Index) => new AutoStartCandidate(Mapping, Index))
            .GroupBy(candidate => EndpointKey(candidate.Mapping), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var selectedByEndpoint = new Dictionary<string, AutoStartCandidate>(StringComparer.OrdinalIgnoreCase);
        var reasonByEndpoint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (endpoint, candidates) in endpointGroups)
        {
            AutoStartCandidate? selected = null;
            if (_lastRunningByEndpoint.TryGetValue(endpoint, out var lastRunningId))
            {
                selected = candidates.LastOrDefault(candidate => string.Equals(candidate.Mapping.Id, lastRunningId, StringComparison.OrdinalIgnoreCase));
            }

            if (selected is null)
            {
                selected = candidates[^1];
                reasonByEndpoint[endpoint] = "no last-running history exists; using the last configured AutoStart mapping as fallback";
            }
            else
            {
                reasonByEndpoint[endpoint] = "it is the last mapping that successfully reached Running for this endpoint";
            }

            selectedByEndpoint[endpoint] = selected;
        }

        var selectedMappings = new List<TunnelMapping>();
        var addedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            var endpoint = EndpointKey(mapping);
            var winner = selectedByEndpoint[endpoint];
            if (!string.Equals(winner.Mapping.Id, mapping.Id, StringComparison.OrdinalIgnoreCase))
            {
                StopExisting(mapping.Id);
                _lastProcessErrors.TryRemove(mapping.Id, out _);
                ResetRestartBackoff(mapping.Id);
                _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Stopped, null, null, null, null);
                _log.Info(
                    mapping.Name,
                    $"AutoStart skipped because {winner.Mapping.Name} ({winner.Mapping.VisiblePort}) is selected for RFC2217 endpoint {FormatEndpoint(mapping)}: {reasonByEndpoint[endpoint]}.");
                continue;
            }

            if (addedEndpoints.Add(endpoint))
            {
                selectedMappings.Add(mapping);
            }
        }

        return selectedMappings;
    }

    public async Task<TunnelStatus> StartAsync(string id, CancellationToken cancellationToken = default)
    {
        return await StartAsync(
            id,
            resetRestartBackoff: true,
            logStartupFailure: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TunnelStatus> StartAsync(
        string id,
        bool resetRestartBackoff,
        bool logStartupFailure,
        CancellationToken cancellationToken = default)
    {
        var mappingLock = GetMappingLock(id);
        await mappingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (resetRestartBackoff)
            {
                ResetRestartBackoff(id);
            }

            return await StartCoreAsync(id, cancellationToken, logStartupFailure).ConfigureAwait(false);
        }
        finally
        {
            mappingLock.Release();
        }
    }

    private async Task<TunnelStatus> StartCoreAsync(string id, CancellationToken cancellationToken, bool logStartupFailure)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        var mapping = config.Mappings.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mapping '{id}' was not found.");

        if (mapping.Backend == TunnelBackend.Kmdf)
        {
            StopEndpointConflicts(mapping);
            return await StartKmdfAsync(mapping, cancellationToken, logStartupFailure);
        }

        if (mapping.Backend == TunnelBackend.Com0comService)
        {
            return await StartCom0comServiceAsync(mapping, cancellationToken, logStartupFailure);
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
        RecordRunning(mapping);
        _log.Info(mapping.Name, $"Started hub4com process {process.Id}.");
        ResetRestartBackoff(id);

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
            ResetRestartBackoff(id);
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
        CancellationToken cancellationToken,
        bool logStartupFailure)
    {
        StopExisting(mapping.Id);

        var effectiveMapping = mapping with { SuppressInitialControlLineSync = true };
        return await StartManagedSessionAsync(
            mapping,
            () => _kmdfSessionFactory(effectiveMapping, _log, (faultedSession, error) => OnKmdfFaulted(mapping, faultedSession, error)),
            cancellationToken,
            logStartupFailure: logStartupFailure);
    }

    private async Task<TunnelStatus> StartCom0comServiceAsync(
        TunnelMapping mapping,
        CancellationToken cancellationToken,
        bool logStartupFailure)
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

        return await StartManagedSessionAsync(
            mapping,
            () => _com0comServiceSessionFactory(mapping, _log, (faultedSession, error) => OnSessionFaulted(mapping, faultedSession, error)),
            cancellationToken,
            retryBackingPortRelease: true,
            logStartupFailure: logStartupFailure);
    }

    private async Task<TunnelStatus> StartManagedSessionAsync(
        TunnelMapping mapping,
        Func<IManagedTunnelSession> sessionFactory,
        CancellationToken cancellationToken,
        bool retryBackingPortRelease = false,
        bool logStartupFailure = true)
    {
        var retryUntil = DateTimeOffset.UtcNow + _portReleaseRetryTimeout;
        while (true)
        {
            var session = sessionFactory();
            _tunnels[mapping.Id] = new ManagedTunnel(mapping, TunnelRunState.Starting, null, session, DateTimeOffset.UtcNow, null);

            try
            {
                await StartSessionWithTimeoutAsync(mapping, session, cancellationToken).ConfigureAwait(false);
                if (!IsCurrentSession(mapping.Id, session))
                {
                    session.Dispose();
                    _log.Info(mapping.Name, $"Ignored stale {FormatBackendForLog(mapping.Backend)} startup completion after the tunnel was stopped or replaced.");
                    return CurrentStatusOrStopped(mapping);
                }

                var running = new ManagedTunnel(mapping, TunnelRunState.Running, null, session, DateTimeOffset.UtcNow, null);
                _tunnels[mapping.Id] = running;
                RecordRunning(mapping);
                ResetRestartBackoff(mapping.Id);
                return running.ToStatus();
            }
            catch (Exception ex) when (retryBackingPortRelease
                && IsBackingPortAccessDeniedError(ex.Message)
                && DateTimeOffset.UtcNow < retryUntil)
            {
                session.Dispose();
                if (!IsCurrentSession(mapping.Id, session))
                {
                    _log.Info(mapping.Name, $"Ignored stale {FormatBackendForLog(mapping.Backend)} startup retry after the tunnel was stopped or replaced: {ex.Message}");
                    return CurrentStatusOrStopped(mapping);
                }

                _log.Info(
                    mapping.Name,
                    $"Backing port {mapping.BackingPort} is still busy after stopping the previous backend; retrying in {_portReleaseRetryDelay.TotalMilliseconds:0} ms.");
                await Task.Delay(_portReleaseRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                if (!IsCurrentSession(mapping.Id, session))
                {
                    _log.Info(mapping.Name, $"Ignored stale {FormatBackendForLog(mapping.Backend)} startup failure after the tunnel was stopped or replaced: {ex.Message}");
                    return CurrentStatusOrStopped(mapping);
                }

                var faulted = new ManagedTunnel(mapping, TunnelRunState.Faulted, null, null, null, ex.Message);
                _tunnels[mapping.Id] = faulted;
                if (logStartupFailure)
                {
                    _log.Error(mapping.Name, ex.Message);
                }

                var scheduled = ScheduleSessionRestart(mapping, ex.Message);
                if (!logStartupFailure && !scheduled)
                {
                    _log.Error(mapping.Name, ex.Message);
                }

                return faulted.ToStatus();
            }
        }
    }

    private async Task StartSessionWithTimeoutAsync(TunnelMapping mapping, IManagedTunnelSession session, CancellationToken cancellationToken)
    {
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startTask = Task.Run(() => session.StartAsync(startCts.Token), CancellationToken.None);
        var timeoutTask = Task.Delay(_sessionStartTimeout, cancellationToken);
        var completed = await Task.WhenAny(startTask, timeoutTask).ConfigureAwait(false);
        if (completed == startTask)
        {
            await startTask.ConfigureAwait(false);
            return;
        }

        startCts.Cancel();
        ObserveLateSessionStart(startTask);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new TimeoutException($"{FormatBackendForLog(mapping.Backend)} startup timed out after {FormatRestartDelay(_sessionStartTimeout)}.");
    }

    private static void ObserveLateSessionStart(Task startTask)
    {
        _ = startTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool IsCurrentSession(string id, IManagedTunnelSession session)
    {
        return _tunnels.TryGetValue(id, out var existing) && ReferenceEquals(existing.Session, session);
    }

    private TunnelStatus CurrentStatusOrStopped(TunnelMapping mapping)
    {
        return _tunnels.TryGetValue(mapping.Id, out var existing)
            ? existing.ToStatus()
            : new ManagedTunnel(mapping, TunnelRunState.Stopped, null, null, null, null).ToStatus();
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

        var currentMapping = existing.Mapping;
        _tunnels[currentMapping.Id] = new ManagedTunnel(currentMapping, TunnelRunState.Faulted, null, null, null, error);
        ScheduleSessionRestart(currentMapping, error);
    }

    private bool ScheduleSessionRestart(TunnelMapping mapping, string error)
    {
        if (!mapping.RestartOnFailure || IsPermanentSessionError(error))
        {
            return false;
        }

        ScheduleRestart(mapping, error, FormatBackendForLog(mapping.Backend));
        return true;
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
            ResetRestartBackoff(conflict.Mapping.Id);
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

        if (!_tunnels.TryGetValue(mapping.Id, out var existing) || !ReferenceEquals(existing.Process, process))
        {
            _log.Info(mapping.Name, $"Ignored stale hub4com process {process.Id} exit after the tunnel was stopped or replaced.");
            return;
        }

        var currentMapping = existing.Mapping;
        var message = BuildExitMessage(currentMapping, process);
        _log.Warn(currentMapping.Name, message);
        _tunnels[currentMapping.Id] = new ManagedTunnel(currentMapping, TunnelRunState.Faulted, null, null, null, message);

        if (!currentMapping.RestartOnFailure || IsPermanentProcessError(message))
        {
            return;
        }

        ScheduleRestart(currentMapping, message, FormatBackendForLog(currentMapping.Backend));
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
            || value.Contains("ERROR 5", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || value.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase);
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
        var accessDenied = value.Contains("ERROR 5", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || value.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase);
        if (!accessDenied)
        {
            return false;
        }

        return value.Contains("CreateFile", StringComparison.OrdinalIgnoreCase)
            || value.Contains("serial port", StringComparison.OrdinalIgnoreCase)
            || value.Contains("backing port", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CNC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermanentSessionError(string value)
    {
        return value.Contains("control channel", StringComparison.OrdinalIgnoreCase)
            || value.Contains("driver protocol", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ack returned unexpected value", StringComparison.OrdinalIgnoreCase)
            || value.Contains("only available on Windows", StringComparison.OrdinalIgnoreCase)
            || value.Contains("backingPort is required", StringComparison.OrdinalIgnoreCase)
            || IsBackingPortAccessDeniedError(value);
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

    private void LoadRuntimeState()
    {
        try
        {
            if (!File.Exists(_runtimeStatePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_runtimeStatePath));
            if (state is null)
            {
                return;
            }

            foreach (var (endpoint, mappingId) in state)
            {
                if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(mappingId))
                {
                    _lastRunningByEndpoint[endpoint] = mappingId;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn("runtime", $"Could not load runtime state: {ex.Message}");
        }
    }

    private void RecordRunning(TunnelMapping mapping)
    {
        _lastRunningByEndpoint[EndpointKey(mapping)] = mapping.Id;
        SaveRuntimeState();
    }

    private void SaveRuntimeState()
    {
        try
        {
            lock (_runtimeStateLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_runtimeStatePath)!);
                var tempPath = _runtimeStatePath + ".tmp";
                var snapshot = _lastRunningByEndpoint.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, _runtimeStatePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("runtime", $"Could not save runtime state: {ex.Message}");
        }
    }
    private static bool UsesSameEndpoint(TunnelMapping left, TunnelMapping right)
    {
        return string.Equals(EndpointKey(left), EndpointKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string EndpointKey(TunnelMapping mapping)
    {
        return $"{mapping.Protocol}|{mapping.Host.Trim()}|{mapping.Port}";
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

    private void ResetRestartBackoff(string id)
    {
        _restartBackoffs.TryRemove(id, out _);
    }

    private RestartBackoffState AdvanceRestartBackoff(string id, string error)
    {
        return _restartBackoffs.AddOrUpdate(
            id,
            _ => new RestartBackoffState(1, _restartInitialDelay, error),
            (_, previous) => string.Equals(previous.LastError, error, StringComparison.Ordinal)
                ? new RestartBackoffState(previous.Attempt + 1, DoubleRestartDelay(previous.Delay), error)
                : new RestartBackoffState(1, _restartInitialDelay, error));
    }

    private TimeSpan DoubleRestartDelay(TimeSpan current)
    {
        if (current >= _restartMaxDelay || current.Ticks > _restartMaxDelay.Ticks / 2)
        {
            return _restartMaxDelay;
        }

        return TimeSpan.FromTicks(current.Ticks * 2);
    }

    private void ScheduleRestart(TunnelMapping mapping, string error, string backendName)
    {
        var restartVersion = GetRestartVersion(mapping.Id);
        var backoff = AdvanceRestartBackoff(mapping.Id, error);
        if (backoff.Attempt <= RestartScheduleLogAttempts)
        {
            _log.Warn(
                mapping.Name,
                $"Scheduling {backendName} restart attempt {backoff.Attempt} in {FormatRestartDelay(backoff.Delay)} after: {error}");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(backoff.Delay);
            if (GetRestartVersion(mapping.Id) != restartVersion)
            {
                return;
            }

            try
            {
                await StartAsync(
                    mapping.Id,
                    resetRestartBackoff: false,
                    logStartupFailure: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(mapping.Name, $"{backendName} restart failed: {ex.Message}");
            }
        });
    }

    private static string FormatRestartDelay(TimeSpan delay)
    {
        return delay.TotalSeconds >= 1
            ? $"{delay.TotalSeconds:0.#} s"
            : $"{delay.TotalMilliseconds:0} ms";
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

    private sealed record RestartBackoffState(
        int Attempt,
        TimeSpan Delay,
        string LastError);

    private sealed record AutoStartCandidate(TunnelMapping Mapping, int Index);

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
