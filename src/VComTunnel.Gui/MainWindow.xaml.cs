using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using VComTunnel.Core;

namespace VComTunnel.Gui;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _client = new() { BaseAddress = new Uri("http://127.0.0.1:44817") };
    private readonly ObservableCollection<MappingRow> _mappings = [];
    private readonly List<string> _guiLogLines = [];
    private Process? _ownedServiceProcess;
    private bool _serviceStartAttempted;
    private bool _dependencyPollActive;

    public MainWindow()
    {
        InitializeComponent();
        MappingsGrid.ItemsSource = _mappings;
        _ = InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopOwnedServiceProcess();
        _client.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _serviceStartAttempted = false;
        await RefreshAsync();
    }

    private async void SetupDeps_Click(object sender, RoutedEventArgs e) => await SetupDependenciesAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void Start_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("stop");
    private async void Ports_Click(object sender, RoutedEventArgs e) => await ShowCom0comPairsAsync();
    private async void CreatePair_Click(object sender, RoutedEventArgs e) => await CreatePairForSelectedAsync();
    private async void DeletePair_Click(object sender, RoutedEventArgs e) => await DeletePairForSelectedAsync();
    private async void ClearLogs_Click(object sender, RoutedEventArgs e) => await ClearLogsAsync();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var portNumber = 12 + _mappings.Count;
        _mappings.Add(new MappingRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Tunnel {_mappings.Count + 1}",
            Backend = "com0comHub4com",
            VisiblePort = $"COM{portNumber}",
            BackingPort = $"CNCB{portNumber}",
            Host = "127.0.0.1",
            Port = 3333,
            AutoStart = false,
            RestartOnFailure = true
        });
    }

    private async Task RefreshAsync()
    {
        if (!await IsServiceReadyAsync())
        {
            await StartServiceAndRefreshAsync(force: false);
            return;
        }

        try
        {
            ServiceStateText.Text = "Service: connecting...";
            var mappings = await _client.GetFromJsonAsync<List<TunnelMapping>>("/api/mappings", JsonOptions) ?? [];
            _mappings.Clear();
            foreach (var mapping in mappings)
            {
                _mappings.Add(MappingRow.From(mapping));
            }

            var dependencies = await _client.GetFromJsonAsync<SystemDependencyReport>("/api/dependencies", JsonOptions);
            DependenciesText.Text = FormatDependencies(dependencies);
            var localDependencies = new DependencyDetector().Detect();
            if (dependencies?.IsReadyForCom0comHub4com == false && localDependencies.IsReadyForCom0comHub4com)
            {
                SetStatus("Service dependency cache is stale. Restart VComTunnel.Service or close/reopen the GUI.", "warn");
                DependenciesText.Text += Environment.NewLine + "Local detector is ready, but the running service is not. The service process is likely stale.";
                DependenciesText.Text += Environment.NewLine + Environment.NewLine + "Local detector:";
                DependenciesText.Text += Environment.NewLine + FormatDependencies(localDependencies);
            }
            await RefreshLogsAsync();
            if (!ServiceStateText.Text.Contains("stale", StringComparison.OrdinalIgnoreCase))
            {
                ServiceStateText.Text = $"Service: connected, {_mappings.Count} mapping(s)";
            }
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = "Service: offline";
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
        }
    }

    private async Task InitializeAsync()
    {
        ServiceStateText.Text = "Service: checking...";
        if (!await IsServiceReadyAsync())
        {
            await StartServiceAndRefreshAsync(force: false);
            return;
        }

        await RefreshAsync();
    }

    private async Task<bool> IsServiceReadyAsync()
    {
        try
        {
            using var response = await _client.GetAsync("/api/status");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartServiceAndRefreshAsync(bool force)
    {
        if (_serviceStartAttempted && !force)
        {
            ServiceStateText.Text = "Service: offline";
            DependenciesText.Text = "VComTunnel.Service is not reachable at 127.0.0.1:44817. Click Refresh to retry auto-start, or run VComTunnel.Service manually.";
            DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return;
        }

        _serviceStartAttempted = true;
        ServiceStateText.Text = "Service: starting...";

        var servicePath = ResolveServicePath();
        if (servicePath is null)
        {
            ServiceStateText.Text = "Service: offline";
            DependenciesText.Text = "Could not find VComTunnel.Service.exe near the GUI. Build the solution or start the service manually.";
            DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var logPath = Path.Combine(AppPaths.LogsDirectory, "gui-started-service.log");
            _ownedServiceProcess = Process.Start(new ProcessStartInfo
            {
                FileName = servicePath,
                ArgumentList = { "--console" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(servicePath)!
            });
            if (_ownedServiceProcess is not null)
            {
                _ownedServiceProcess.OutputDataReceived += (_, e) => AppendServiceStartLog(logPath, e.Data);
                _ownedServiceProcess.ErrorDataReceived += (_, e) => AppendServiceStartLog(logPath, e.Data);
                _ownedServiceProcess.BeginOutputReadLine();
                _ownedServiceProcess.BeginErrorReadLine();
            }
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = "Service: start failed";
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return;
        }

        if (await WaitForServiceAsync(TimeSpan.FromSeconds(10)))
        {
            await RefreshAsync();
            return;
        }

        ServiceStateText.Text = "Service: offline";
        DependenciesText.Text = $"Started {servicePath}, but 127.0.0.1:44817 did not become ready within 10 seconds.";
        DependenciesText.Text += Environment.NewLine + $"Startup log: {Path.Combine(AppPaths.LogsDirectory, "gui-started-service.log")}";
        DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
    }

    private async Task<bool> WaitForServiceAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsServiceReadyAsync())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static string? ResolveServicePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "VComTunnel.Service.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "VComTunnel.Service", "bin", "Debug", "net8.0-windows", "VComTunnel.Service.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "VComTunnel.Service", "bin", "Release", "net8.0-windows", "VComTunnel.Service.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string BuildOfflineMessage(Exception ex)
    {
        return $"VComTunnel.Service is not reachable at 127.0.0.1:44817.\r\n\r\n{ex.Message}\r\n\r\nClick Refresh to retry auto-start, or run VComTunnel.Service manually.";
    }

    private static void AppendServiceStartLog(string logPath, string? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void StopOwnedServiceProcess()
    {
        try
        {
            if (_ownedServiceProcess is { HasExited: false })
            {
                _ownedServiceProcess.Kill(entireProcessTree: true);
                _ownedServiceProcess.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            _ownedServiceProcess?.Dispose();
            _ownedServiceProcess = null;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var saved = await SaveMappingsAsync();
            ServiceStateText.Text = saved ? "Saved." : ServiceStateText.Text;
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = $"Save failed: {ex.Message}";
        }
    }

    private async Task PostSelectedAsync(string action)
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            ServiceStateText.Text = "Select a mapping first.";
            return;
        }

        try
        {
            if (row.IsKmdf)
            {
                SetStatus("KMDF backend is only a scaffold. Use com0comHub4com for now.");
                return;
            }

            if (await IsServiceDependencyStateStaleAsync())
            {
                SetStatus("Service dependency state is stale. Close/reopen the GUI, then retry Start.", "warn");
                return;
            }

            if (!await SaveMappingsAsync())
            {
                return;
            }

            if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase)
                && !await EnsurePairExistsBeforeStartAsync(row))
            {
                return;
            }

            var response = await _client.PostAsync($"/api/mappings/{row.Id}/{action}", null);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await RefreshAsync();
                SetStatus($"{action}: mapping was not found after save. Select the row again and retry.");
                return;
            }

            var message = FormatActionResult(action, response, responseBody);
            SetStatus(message, response.IsSuccessStatusCode ? "info" : "error");
            AppendGuiLog("debug", "api", responseBody);
            await RefreshLogsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"{action} failed: {ex.Message}");
        }
    }

    private async Task<bool> SaveMappingsAsync()
    {
        CommitGridEdits();
        NormalizeRowsBeforeSave();
        var mappings = _mappings.Select(r => r.ToMapping()).ToList();
        var response = await _client.PutAsJsonAsync("/api/mappings", mappings, JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            AppendGuiLog("info", "gui", $"Saved {_mappings.Count} mapping(s).");
            return true;
        }

        SetStatus($"Save failed: {body}");
        return false;
    }

    private async Task ShowCom0comPairsAsync()
    {
        try
        {
            if (!await IsServiceReadyAsync())
            {
                await StartServiceAndRefreshAsync(force: false);
                return;
            }

            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            DependenciesText.Text = FormatCom0comPairs(pairs);
            SetStatus($"Found {pairs.Count} com0com pair(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Ports failed: {ex.Message}", "error");
        }
    }

    private async Task CreatePairForSelectedAsync()
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            ServiceStateText.Text = "Select a mapping first.";
            return;
        }

        try
        {
            if (row.IsKmdf)
            {
                SetStatus("KMDF mappings do not use com0com pairs.");
                return;
            }

            if (!await SaveMappingsAsync())
            {
                return;
            }

            var answer = MessageBox.Show(
                $"Create com0com pair {row.VisiblePort} <-> {row.BackingPort}?\r\n\r\nThis launches setupc.exe with administrator approval.",
                "VComTunnel COM pair",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await _client.PostAsync($"/api/com0com/mappings/{row.Id}/create-plan", null);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus($"Create pair failed: {ExtractError(body)}", "error");
                return;
            }

            var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
            if (plan is null)
            {
                SetStatus("Create pair failed: empty setupc plan.", "error");
                return;
            }

            LaunchSetupcPlan(plan);
            _ = PollCom0comPairsAfterSetupcAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Create pair failed: {ex.Message}", "error");
        }
    }

    private async Task DeletePairForSelectedAsync()
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            ServiceStateText.Text = "Select a mapping first.";
            return;
        }

        try
        {
            CommitGridEdits();
            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            var pair = pairs.FirstOrDefault(p => PairMatchesMapping(p, row));
            if (pair is null)
            {
                DependenciesText.Text = FormatCom0comPairs(pairs);
                SetStatus($"No registered com0com pair matches {row.VisiblePort} <-> {row.BackingPort}.", "warn");
                return;
            }

            var answer = MessageBox.Show(
                $"Delete com0com pair {pair.PairNumber}: {pair.PortA} <-> {pair.PortB}?\r\n\r\nStop tools using these ports before continuing.",
                "VComTunnel COM pair",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await _client.PostAsync($"/api/com0com/pairs/{pair.PairNumber}/remove-plan", null);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus($"Delete pair failed: {ExtractError(body)}", "error");
                return;
            }

            var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
            if (plan is null)
            {
                SetStatus("Delete pair failed: empty setupc plan.", "error");
                return;
            }

            LaunchSetupcPlan(plan);
            _ = PollCom0comPairsAfterSetupcAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Delete pair failed: {ex.Message}", "error");
        }
    }

    private void LaunchSetupcPlan(SetupcCommandPlan plan)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory ?? Path.GetDirectoryName(plan.FileName) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = plan.RequiresElevation ? "runas" : ""
        });

        SetStatus($"Launched setupc: {plan.Arguments}");
    }

    private async Task<bool> EnsurePairExistsBeforeStartAsync(MappingRow row)
    {
        var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
        if (pairs.Any(pair => PairMatchesMapping(pair, row)))
        {
            return true;
        }

        DependenciesText.Text = FormatCom0comPairs(pairs);
        var answer = MessageBox.Show(
            $"The com0com pair {row.VisiblePort} <-> {row.BackingPort} does not exist.\r\n\r\nCreate it now and then start this tunnel?",
            "VComTunnel COM pair",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetStatus($"Start canceled: missing com0com pair {row.VisiblePort} <-> {row.BackingPort}.", "warn");
            return false;
        }

        var response = await _client.PostAsync($"/api/com0com/mappings/{row.Id}/create-plan", null);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            SetStatus($"Create pair failed: {ExtractError(body)}", "error");
            return false;
        }

        var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
        if (plan is null)
        {
            SetStatus("Create pair failed: empty setupc plan.", "error");
            return false;
        }

        LaunchSetupcPlan(plan);
        SetStatus("Waiting for com0com pair to appear...");
        if (await WaitForPairAsync(row, TimeSpan.FromSeconds(45)))
        {
            SetStatus($"Created com0com pair {row.VisiblePort} <-> {row.BackingPort}. Starting...");
            return true;
        }

        SetStatus($"Pair creation was not detected. Click Ports to inspect, then Start again.", "warn");
        return false;
    }

    private async Task<bool> WaitForPairAsync(MappingRow row, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            DependenciesText.Text = FormatCom0comPairs(pairs);
            if (pairs.Any(pair => PairMatchesMapping(pair, row)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task PollCom0comPairsAfterSetupcAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (!await IsServiceReadyAsync())
        {
            return;
        }

        try
        {
            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            DependenciesText.Text = FormatCom0comPairs(pairs);
        }
        catch
        {
        }
    }

    private void CommitGridEdits()
    {
        MappingsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        MappingsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private void NormalizeRowsBeforeSave()
    {
        foreach (var row in _mappings.Where(row => !row.IsKmdf))
        {
            if (string.Equals(row.VisiblePort, row.BackingPort, StringComparison.OrdinalIgnoreCase)
                && TryGetComPortNumber(row.VisiblePort, out var portNumber))
            {
                row.BackingPort = $"CNCB{portNumber}";
                AppendGuiLog("warn", "gui", $"{row.Name}: Backing cannot equal Visible; changed backing to {row.BackingPort}.");
            }
        }

        MappingsGrid.Items.Refresh();
    }

    private static bool TryGetComPortNumber(string? port, out int portNumber)
    {
        portNumber = 0;
        return !string.IsNullOrWhiteSpace(port)
            && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(port[3..], out portNumber)
            && portNumber > 0;
    }

    private async Task<bool> IsServiceDependencyStateStaleAsync()
    {
        try
        {
            var serviceReport = await _client.GetFromJsonAsync<SystemDependencyReport>("/api/dependencies", JsonOptions);
            var localReport = new DependencyDetector().Detect();
            return serviceReport?.IsReadyForCom0comHub4com == false && localReport.IsReadyForCom0comHub4com;
        }
        catch
        {
            return false;
        }
    }

    private async Task SetupDependenciesAsync()
    {
        try
        {
            ServiceStateText.Text = "Checking dependencies...";
            var localReport = new DependencyDetector().Detect();
            DependenciesText.Text = FormatDependencies(localReport);
            if (localReport.IsReadyForCom0comHub4com)
            {
                SetStatus("Dependencies already ready.");
                return;
            }

            DependencyInstallResult result;
            if (await IsServiceReadyAsync())
            {
                var response = await _client.PostAsJsonAsync("/api/dependencies/install", new DependencyInstallRequest(), JsonOptions);
                if (!response.IsSuccessStatusCode)
                {
                    SetStatus($"Dependency setup failed: {await response.Content.ReadAsStringAsync()}");
                    return;
                }

                result = await response.Content.ReadFromJsonAsync<DependencyInstallResult>(JsonOptions)
                    ?? new DependencyInstallResult([], localReport);
            }
            else
            {
                result = await new DependencyInstaller(new DependencyDetector()).InstallAsync(new DependencyInstallRequest());
            }

            DependenciesText.Text = FormatInstallResult(result);
            var refreshed = new DependencyDetector().Detect();
            if (refreshed.IsReadyForCom0comHub4com)
            {
                ServiceStateText.Text = "Dependencies ready.";
                AppendGuiLog("info", "gui", "Dependencies ready.");
                DependenciesText.Text += Environment.NewLine + FormatDependencies(refreshed);
                await RefreshAsync();
                return;
            }

            var installer = new DependencyInstaller(new DependencyDetector());
            if (installer.FindCom0comInstaller() is not null)
            {
                var answer = MessageBox.Show(
                    "com0com driver still needs an elevated installer run. Launch it now?",
                    "VComTunnel dependency setup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    LaunchCom0comInstaller();
                    return;
                }
            }

            ServiceStateText.Text = "Dependencies downloaded. com0com driver install is still required.";
            AppendGuiLog("warn", "gui", "Dependencies downloaded. com0com driver install is still required.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Dependency setup failed: {ex.Message}");
        }
    }

    private void LaunchCom0comInstaller()
    {
        try
        {
            var dependencyReport = new DependencyDetector().Detect();
            if (dependencyReport.IsReadyForCom0comHub4com)
            {
                SetStatus("Dependencies already ready.");
                DependenciesText.Text = FormatDependencies(dependencyReport);
                return;
            }

            var installer = new DependencyInstaller(new DependencyDetector());
            if (installer.LaunchCom0comInstaller(out var message))
            {
                SetStatus(message);
                _ = PollDependenciesAfterInstallerAsync();
            }
            else
            {
                SetStatus(message);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not launch com0com installer: {ex.Message}");
        }
    }

    private async Task PollDependenciesAfterInstallerAsync()
    {
        if (_dependencyPollActive)
        {
            return;
        }

        _dependencyPollActive = true;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (!await IsServiceReadyAsync())
                {
                    continue;
                }

                var dependencies = await _client.GetFromJsonAsync<SystemDependencyReport>("/api/dependencies", JsonOptions);
                DependenciesText.Text = FormatDependencies(dependencies);
                await RefreshLogsAsync();

                if (dependencies?.IsReadyForCom0comHub4com == true)
                {
                    SetStatus("Dependencies ready.");
                    await RefreshAsync();
                    return;
                }

                ServiceStateText.Text = "Waiting for com0com installer to finish...";
            }

            SetStatus("Dependency refresh timed out. Click Refresh after the installer finishes.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Dependency refresh failed: {ex.Message}");
        }
        finally
        {
            _dependencyPollActive = false;
        }
    }

    private async Task RefreshLogsAsync()
    {
        var logs = await _client.GetFromJsonAsync<List<LogEntry>>("/api/logs", JsonOptions) ?? [];
        var serviceLines = logs.Select(l => $"{l.Timestamp:HH:mm:ss} {l.Level,-5} {l.Source}: {l.Message}");
        LogsText.Text = string.Join(Environment.NewLine, serviceLines.Concat(_guiLogLines));
        LogsText.ScrollToEnd();
    }

    private async Task ClearLogsAsync()
    {
        try
        {
            _guiLogLines.Clear();
            if (await IsServiceReadyAsync())
            {
                using var response = await _client.DeleteAsync("/api/logs");
                if (!response.IsSuccessStatusCode)
                {
                    ServiceStateText.Text = $"Clear logs failed: {(int)response.StatusCode}";
                    return;
                }
            }

            LogsText.Clear();
            ServiceStateText.Text = "Logs cleared.";
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = $"Clear logs failed: {ex.Message}";
        }
    }

    private void SetStatus(string message, string level = "info")
    {
        ServiceStateText.Text = message;
        AppendGuiLog(level, "gui", message);
    }

    private void AppendGuiLog(string level, string source, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {level,-5} {source}: {message}";
        _guiLogLines.Add(line);
        if (_guiLogLines.Count > 300)
        {
            _guiLogLines.RemoveRange(0, _guiLogLines.Count - 300);
        }

        LogsText.Text = string.IsNullOrWhiteSpace(LogsText.Text)
            ? line
            : LogsText.Text + Environment.NewLine + line;
        LogsText.ScrollToEnd();
    }

    private static string FormatDependencies(SystemDependencyReport? report)
    {
        if (report is null)
        {
            return "No dependency report.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"com0com/hub4com ready: {report.IsReadyForCom0comHub4com}");
        builder.AppendLine($"KMDF install tooling ready: {report.IsReadyForKmdf}");
        builder.AppendLine();
        foreach (var item in report.Items)
        {
            builder.AppendLine($"{(item.Found ? "OK" : "MISS")} {item.Name}");
            builder.AppendLine(item.Path ?? item.Message);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatInstallResult(DependencyInstallResult result)
    {
        var builder = new StringBuilder();
        foreach (var step in result.Steps)
        {
            builder.AppendLine($"{(step.Success ? "OK" : "FAIL")} {step.Name}");
            builder.AppendLine(step.Message);
            if (!string.IsNullOrWhiteSpace(step.Path))
            {
                builder.AppendLine(step.Path);
            }
            builder.AppendLine();
        }

        builder.AppendLine(FormatDependencies(result.DependencyReport));
        return builder.ToString();
    }

    private static string FormatCom0comPairs(IReadOnlyList<Com0comPairInfo> pairs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("com0com pairs");
        builder.AppendLine();
        if (pairs.Count == 0)
        {
            builder.AppendLine("No com0com pairs found in Windows COM database.");
            return builder.ToString();
        }

        foreach (var pair in pairs)
        {
            var state = pair.IsComplete ? "OK" : "PARTIAL";
            builder.AppendLine($"{state} pair {pair.PairNumber}: {pair.PortA ?? "(missing A)"} <-> {pair.PortB ?? "(missing B)"}");
            if (!string.IsNullOrWhiteSpace(pair.DeviceA))
            {
                builder.AppendLine($"  A: {pair.DeviceA}");
            }

            if (!string.IsNullOrWhiteSpace(pair.DeviceB))
            {
                builder.AppendLine($"  B: {pair.DeviceB}");
            }

            builder.AppendLine($"  delete: setupc.exe remove {pair.PairNumber}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool PairMatchesMapping(Com0comPairInfo pair, MappingRow row)
    {
        return PairHasPort(pair, row.VisiblePort) && PairHasPort(pair, row.BackingPort);
    }

    private static bool PairHasPort(Com0comPairInfo pair, string? port)
    {
        return !string.IsNullOrWhiteSpace(port)
            && (string.Equals(pair.PortA, port, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.PortB, port, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatActionResult(string action, HttpResponseMessage response, string responseBody)
    {
        if (!response.IsSuccessStatusCode)
        {
            return $"{action}: {(int)response.StatusCode} {ExtractError(responseBody)}";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            var processId = root.TryGetProperty("processId", out var processElement) && processElement.ValueKind != JsonValueKind.Null
                ? processElement.GetRawText()
                : null;
            var lastError = root.TryGetProperty("lastError", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null
                ? errorElement.GetString()
                : null;

            var suffix = string.IsNullOrWhiteSpace(processId) ? "" : $" pid={processId}";
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                suffix += $" ({lastError})";
            }

            return $"{action}: {state ?? response.StatusCode.ToString()}{suffix}";
        }
        catch
        {
            return $"{action}: {(int)response.StatusCode}";
        }
    }

    private static string ExtractError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? responseBody;
            }
        }
        catch
        {
        }

        return responseBody;
    }
}

public sealed class MappingRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Backend { get; set; } = "com0comHub4com";
    public string VisiblePort { get; set; } = "";
    public string? BackingPort { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool AutoStart { get; set; }
    public bool RestartOnFailure { get; set; } = true;
    public bool IsKmdf => string.Equals(Backend, "kmdf", StringComparison.OrdinalIgnoreCase);

    public static MappingRow From(TunnelMapping mapping)
    {
        return new MappingRow
        {
            Id = mapping.Id,
            Name = mapping.Name,
            Backend = mapping.Backend == TunnelBackend.Kmdf ? "kmdf" : "com0comHub4com",
            VisiblePort = mapping.VisiblePort,
            BackingPort = mapping.BackingPort,
            Host = mapping.Host,
            Port = mapping.Port,
            AutoStart = mapping.AutoStart,
            RestartOnFailure = mapping.RestartOnFailure
        };
    }

    public TunnelMapping ToMapping()
    {
        var backend = string.Equals(Backend, "kmdf", StringComparison.OrdinalIgnoreCase)
            ? TunnelBackend.Kmdf
            : TunnelBackend.Com0comHub4com;

        return new TunnelMapping
        {
            Id = Id,
            Name = Name,
            Backend = backend,
            VisiblePort = VisiblePort,
            BackingPort = backend == TunnelBackend.Kmdf ? null : BackingPort,
            Host = Host,
            Port = Port,
            AutoStart = AutoStart,
            RestartOnFailure = RestartOnFailure
        };
    }
}
