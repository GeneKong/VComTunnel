using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MahApps.Metro.IconPacks;
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
    private readonly ObservableCollection<ComPairRow> _comPairs = [];
    private readonly List<string> _guiLogLines = [];
    private Process? _ownedServiceProcess;
    private bool _serviceStartAttempted;
    private bool _dependencyPollActive;
    private bool _updatingLanguage;
    private bool _updatingSelection;
    private UiLanguage _language = GuiText.DefaultLanguage;

    public MainWindow()
    {
        InitializeComponent();
        MappingsGrid.ItemsSource = _mappings;
        ComPairsList.ItemsSource = _comPairs;
        ApplyLocalization();
        UpdateMappingCommandState();
        _ = InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopOwnedServiceProcess();
        _client.Dispose();
    }

    private string T(string key) => GuiText.Get(_language, key);

    private string TF(string key, params object?[] args) => GuiText.Format(_language, key, args);

    private void SetLanguageMenuSelection()
    {
        _updatingLanguage = true;
        EnglishLanguageMenuItem.IsChecked = _language == UiLanguage.English;
        ChineseLanguageMenuItem.IsChecked = _language == UiLanguage.Chinese;
        _updatingLanguage = false;
    }

    private void ApplyLocalization()
    {
        ServiceMenuItem.Header = T("Menu.Service");
        MappingsMenuItem.Header = T("Menu.Mappings");
        ComPairsMenuItem.Header = T("Menu.ComPairs");
        ToolsMenuItem.Header = T("Menu.Tools");
        LanguageMenuItem.Header = T("Menu.Language");
        EnglishLanguageMenuItem.Header = T("Language.English");
        ChineseLanguageMenuItem.Header = T("Language.Chinese");
        StatusLabel.Text = T("Label.Status");

        SetToolbarButton(RefreshButton, PackIconMaterialKind.Refresh, "Action.Refresh");
        SetToolbarButton(AddButton, PackIconMaterialKind.Plus, "Action.Add");
        SetToolbarButton(SaveButton, PackIconMaterialKind.ContentSave, "Action.Save");
        SetToolbarButton(DeleteMappingButton, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetToolbarButton(StartButton, PackIconMaterialKind.Play, "Action.Start");
        SetToolbarButton(StopButton, PackIconMaterialKind.Stop, "Action.Stop");
        SetToolbarButton(PortsButton, PackIconMaterialKind.FormatListBulleted, "Action.Ports");
        SetToolbarButton(CreatePairButton, PackIconMaterialKind.Link, "Action.CreatePair");
        SetToolbarButton(DeletePairButton, PackIconMaterialKind.DeleteOutline, "Action.DeletePair");
        SetToolbarButton(SetupDepsButton, PackIconMaterialKind.Cog, "Action.SetupDeps");
        SetToolbarButton(ClearLogsButton, PackIconMaterialKind.Broom, "Action.ClearLogs");

        SetCommandMenuItem(RefreshMenuItem, PackIconMaterialKind.Refresh, "Action.Refresh");
        SetCommandMenuItem(AddMenuItem, PackIconMaterialKind.Plus, "Action.Add");
        SetCommandMenuItem(SaveMenuItem, PackIconMaterialKind.ContentSave, "Action.Save");
        SetCommandMenuItem(DeleteMappingMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetCommandMenuItem(StartMappingCommandMenuItem, PackIconMaterialKind.Play, "Action.Start");
        SetCommandMenuItem(StopMappingCommandMenuItem, PackIconMaterialKind.Stop, "Action.Stop");
        SetCommandMenuItem(PortsMenuItem, PackIconMaterialKind.FormatListBulleted, "Action.Ports");
        SetCommandMenuItem(CreatePairMenuItem, PackIconMaterialKind.Link, "Action.CreatePair");
        SetCommandMenuItem(DeletePairMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeletePair");
        SetCommandMenuItem(SetupDepsMenuItem, PackIconMaterialKind.Cog, "Action.SetupDeps");
        SetCommandMenuItem(ClearLogsMenuItem, PackIconMaterialKind.Broom, "Action.ClearLogs");
        SetCommandMenuItem(StartMappingMenuItem, PackIconMaterialKind.Play, "Action.Start");
        SetCommandMenuItem(StopMappingMenuItem, PackIconMaterialKind.Stop, "Action.Stop");
        SetCommandMenuItem(DeleteSelectedMappingMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetCommandMenuItem(DeleteSelectedComPairMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteSelectedPair");

        TunnelMappingsGroupBox.Header = T("Group.Mappings");
        DependenciesGroupBox.Header = T("Group.DependenciesPairs");
        LogsGroupBox.Header = T("Group.Logs");
        NameColumn.Header = T("Column.Name");
        BackendColumn.Header = T("Column.Backend");
        VisibleComColumn.Header = T("Column.VisibleCom");
        BackingColumn.Header = T("Column.Backing");
        HostColumn.Header = T("Column.Host");
        PortColumn.Header = T("Column.Port");
        AutoColumn.Header = T("Column.Auto");
        RestartColumn.Header = T("Column.Restart");
        MappingStateColumn.Header = T("Column.State");
        PairNumberColumn.Header = T("Column.PortType");
        PairPortAColumn.Header = T("Column.PortA");
        PairPortBColumn.Header = T("Column.PortB");
        PairStateColumn.Header = T("Column.State");

        RefreshMappingStateLabels();
        RefreshComPortRowLabels();
        SetLanguageMenuSelection();
        UpdateMappingCommandState();
    }

    private void SetToolbarButton(Button button, PackIconMaterialKind iconKind, string textKey)
    {
        var label = T(textKey);
        button.Content = BuildIcon(iconKind, 18);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private void SetCommandMenuItem(MenuItem item, PackIconMaterialKind iconKind, string textKey)
    {
        item.Header = T(textKey);
        item.Icon = BuildIcon(iconKind, 16);
    }

    private static PackIconMaterial BuildIcon(PackIconMaterialKind iconKind, double size)
    {
        return new PackIconMaterial
        {
            Kind = iconKind,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void RefreshMappingStateLabels()
    {
        foreach (var row in _mappings)
        {
            row.StateLabel = GuiText.State(_language, row.RunState);
        }

        MappingsGrid.Items.Refresh();
        UpdateMappingCommandState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _serviceStartAttempted = false;
        await RefreshAsync();
    }

    private async void SetupDeps_Click(object sender, RoutedEventArgs e) => await SetupDependenciesAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void Ports_Click(object sender, RoutedEventArgs e) => await ShowCom0comPairsAsync();
    private async void CreatePair_Click(object sender, RoutedEventArgs e) => await CreatePairForSelectedAsync();
    private async void DeletePair_Click(object sender, RoutedEventArgs e) => await DeletePairForSelectedAsync();
    private async void DeleteMapping_Click(object sender, RoutedEventArgs e) => await DeleteSelectedMappingAsync();
    private async void ClearLogs_Click(object sender, RoutedEventArgs e) => await ClearLogsAsync();

    private async void StartSelectedMapping_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("start");
    private async void StopSelectedMapping_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("stop");

    private void EnglishLanguageMenuItem_Click(object sender, RoutedEventArgs e) => SetLanguage(UiLanguage.English);

    private void ChineseLanguageMenuItem_Click(object sender, RoutedEventArgs e) => SetLanguage(UiLanguage.Chinese);

    private void SetLanguage(UiLanguage language)
    {
        if (_updatingLanguage)
        {
            return;
        }

        if (_language == language)
        {
            SetLanguageMenuSelection();
            return;
        }

        _language = language;
        ApplyLocalization();
    }

    private async void DeleteSelectedComPair_Click(object sender, RoutedEventArgs e)
    {
        if (ComPairsList.SelectedItem is not ComPairRow row)
        {
            SetStatus(T("Status.SelectPairFirst"), "warn");
            return;
        }

        await DeleteComPortRowAsync(row);
    }

    private void ComPairsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(list, source) is ListViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
            return;
        }

        list.SelectedItem = null;
        UpdateMappingCommandState();
    }

    private void ComPairsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSelection && ComPairsList.SelectedItem is not null)
        {
            _updatingSelection = true;
            MappingsGrid.SelectedItem = null;
            _updatingSelection = false;
        }

        UpdateMappingCommandState();
    }

    private void MappingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSelection && MappingsGrid.SelectedItem is not null)
        {
            _updatingSelection = true;
            ComPairsList.SelectedItem = null;
            _updatingSelection = false;
        }

        UpdateMappingCommandState();
    }

    private async void MappingsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedMappingAsync();
    }

    private void MappingsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(grid, source) is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
            return;
        }

        grid.SelectedItem = null;
        UpdateMappingCommandState();
    }

    private void MappingsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            e.Handled = true;
            return;
        }

        CommitGridEdits();
        UpdateMappingCommandState();
    }

    private void UpdateMappingCommandState()
    {
        var row = MappingsGrid.SelectedItem as MappingRow;
        var port = ComPairsList.SelectedItem as ComPairRow;
        var canStart = row?.CanStart == true;
        var canStop = row?.CanStop == true;
        var canUseMapping = row is not null;
        var canUsePort = row is not null || port is not null;

        DeleteMappingButton.IsEnabled = canUseMapping;
        DeleteMappingMenuItem.IsEnabled = canUseMapping;
        DeleteSelectedMappingMenuItem.IsEnabled = canUseMapping;
        CreatePairButton.IsEnabled = canUseMapping;
        CreatePairMenuItem.IsEnabled = canUseMapping;
        DeletePairButton.IsEnabled = canUsePort;
        DeletePairMenuItem.IsEnabled = canUsePort;
        StartButton.IsEnabled = canStart;
        StopButton.IsEnabled = canStop;
        StartMappingCommandMenuItem.IsEnabled = canStart;
        StopMappingCommandMenuItem.IsEnabled = canStop;
        StartMappingMenuItem.IsEnabled = canStart;
        StopMappingMenuItem.IsEnabled = canStop;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var portNumber = 12 + _mappings.Count;
        var row = new MappingRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Tunnel {_mappings.Count + 1}",
            Backend = "com0comHub4com",
            VisiblePort = $"COM{portNumber}",
            BackingPort = $"CNCB{portNumber}",
            Host = "127.0.0.1",
            Port = 3333,
            AutoStart = false,
            RestartOnFailure = true,
            StateLabel = GuiText.State(_language, TunnelRunState.Stopped)
        };
        _mappings.Add(row);
        MappingsGrid.SelectedItem = row;
        MappingsGrid.ScrollIntoView(row);
        MappingsGrid.Items.Refresh();
        SetStatus(T("Status.AddedMapping"));
        UpdateMappingCommandState();
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
            ServiceStateText.Text = T("Status.ServiceConnecting");
            var dependencyStale = false;
            var mappings = await _client.GetFromJsonAsync<List<TunnelMapping>>("/api/mappings", JsonOptions) ?? [];
            _mappings.Clear();
            foreach (var mapping in mappings)
            {
                _mappings.Add(MappingRow.From(mapping));
            }

            await RefreshMappingStatesAsync();
            var dependencies = await _client.GetFromJsonAsync<SystemDependencyReport>("/api/dependencies", JsonOptions);
            DependenciesText.Text = FormatDependencies(dependencies);
            var localDependencies = new DependencyDetector().Detect();
            if (dependencies?.IsReadyForCom0comHub4com == false && localDependencies.IsReadyForCom0comHub4com)
            {
                dependencyStale = true;
                SetStatus(T("Status.ServiceDependencyCacheStale"), "warn");
                DependenciesText.Text += Environment.NewLine + T("Diag.LocalDetectorStale");
                DependenciesText.Text += Environment.NewLine + Environment.NewLine + T("Diag.LocalDetector");
                DependenciesText.Text += Environment.NewLine + FormatDependencies(localDependencies);
            }
            await RefreshComPairsListAsync(updateDetails: false);
            await RefreshLogsAsync();
            if (!dependencyStale)
            {
                ServiceStateText.Text = TF("Status.ServiceConnected", _mappings.Count);
            }
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = T("Status.ServiceOffline");
            ClearComPairsList();
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
        }
    }

    private async Task InitializeAsync()
    {
        ServiceStateText.Text = T("Status.ServiceChecking");
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
            ServiceStateText.Text = T("Status.ServiceOffline");
            ClearComPairsList();
            DependenciesText.Text = T("Diag.OfflineRetry");
            DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return;
        }

        _serviceStartAttempted = true;
        ServiceStateText.Text = T("Status.ServiceStarting");

        var servicePath = ResolveServicePath();
        if (servicePath is null)
        {
            ServiceStateText.Text = T("Status.ServiceOffline");
            ClearComPairsList();
            DependenciesText.Text = T("Diag.ServiceNotFound");
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
            ServiceStateText.Text = T("Status.ServiceStartFailed");
            ClearComPairsList();
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return;
        }

        if (await WaitForServiceAsync(TimeSpan.FromSeconds(10)))
        {
            await RefreshAsync();
            return;
        }

        ServiceStateText.Text = T("Status.ServiceOffline");
        ClearComPairsList();
        DependenciesText.Text = TF("Diag.StartedButNotReady", servicePath);
        DependenciesText.Text += Environment.NewLine + TF("Diag.StartupLog", Path.Combine(AppPaths.LogsDirectory, "gui-started-service.log"));
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

    private async Task RefreshMappingStatesAsync()
    {
        var status = await _client.GetFromJsonAsync<ServiceStatus>("/api/status", JsonOptions);
        var stateById = status?.Tunnels.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, TunnelStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _mappings)
        {
            row.RunState = stateById.TryGetValue(row.Id, out var tunnel)
                ? tunnel.State
                : TunnelRunState.Stopped;
            row.StateLabel = GuiText.State(_language, row.RunState);
        }

        MappingsGrid.Items.Refresh();
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

    private static string? ResolveCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "VComTunnel.Cli.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "VComTunnel.Cli", "bin", "Debug", "net8.0", "VComTunnel.Cli.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "VComTunnel.Cli", "bin", "Release", "net8.0", "VComTunnel.Cli.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private string BuildOfflineMessage(Exception ex)
    {
        return TF("Diag.Offline", ex.Message);
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
            if (saved)
            {
                await RefreshAsync();
                SetStatus(T("Status.Saved"));
            }
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = TF("Status.SaveFailed", ex.Message);
        }
    }

    private async Task DeleteSelectedMappingAsync()
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            SetStatus(T("Status.SelectMappingFirst"), "warn");
            return;
        }

        var answer = MessageBox.Show(
            TF("Prompt.DeleteMapping", row.Name, row.VisiblePort),
            T("Title.Mapping"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (row.CanStop)
            {
                await _client.PostAsync($"/api/mappings/{row.Id}/stop", null);
            }

            _mappings.Remove(row);
            MappingsGrid.SelectedItem = null;
            if (!await SaveMappingsAsync())
            {
                await RefreshAsync();
                return;
            }

            await RefreshAsync();
            SetStatus(TF("Status.DeletedMapping", row.Name));
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DeleteMappingFailed", ex.Message), "error");
            await RefreshAsync();
        }
    }

    private async Task PostSelectedAsync(string action)
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            ServiceStateText.Text = T("Status.SelectMappingFirst");
            return;
        }

        await PostMappingAsync(row, action);
    }

    private async Task PostMappingAsync(MappingRow row, string action)
    {
        var actionLabel = ActionLabel(action);
        try
        {
            if (await IsServiceDependencyStateStaleAsync())
            {
                SetStatus(T("Status.DependencyStateStale"), "warn");
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
                SetStatus(TF("Status.MappingNotFoundAfterSave", actionLabel));
                return;
            }

            var message = FormatActionResult(action, response, responseBody);
            SetStatus(message, response.IsSuccessStatusCode ? "info" : "error");
            AppendGuiLog("debug", T("Log.Api"), responseBody);
            await RefreshMappingStatesAsync();
            await RefreshLogsAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.ActionFailed", actionLabel, ex.Message));
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
            AppendGuiLog("info", T("Log.Gui"), TF("Status.SavedMappings", _mappings.Count));
            return true;
        }

        SetStatus(TF("Status.SaveFailed", body));
        return false;
    }

    private string ActionLabel(string action)
    {
        return string.Equals(action, "start", StringComparison.OrdinalIgnoreCase)
            ? T("Action.Start")
            : T("Action.Stop");
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

            var pairs = await RefreshComPairsListAsync(updateDetails: false);
            var devices = await GetKmdfDevicesAsync();
            DependenciesText.Text = FormatComPortInventory(pairs, devices);
            SetStatus(TF("Status.FoundPorts", pairs.Count, devices.Count));
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.PortsFailed", ex.Message), "error");
        }
    }

    private async Task CreatePairForSelectedAsync()
    {
        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            ServiceStateText.Text = T("Status.SelectMappingFirst");
            return;
        }

        try
        {
            if (row.IsKmdf)
            {
                await CreateKmdfPortForSelectedAsync(row);
                return;
            }

            if (!await SaveMappingsAsync())
            {
                return;
            }

            var answer = MessageBox.Show(
                TF("Prompt.CreatePair", row.VisiblePort, row.BackingPort),
                T("Title.ComPair"),
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
                SetStatus(TF("Status.CreatePairFailed", ExtractError(body)), "error");
                return;
            }

            var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
            if (plan is null)
            {
                SetStatus(T("Status.CreatePairEmptyPlan"), "error");
                return;
            }

            LaunchSetupcPlan(plan);
            await RefreshComPairsListAsync(updateDetails: true);
            SetStatus(T("Status.WaitingPairAppear"));
            _ = PollCom0comPairsAfterSetupcAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.CreatePairFailed", ex.Message), "error");
        }
    }

    private async Task DeletePairForSelectedAsync()
    {
        if (ComPairsList.SelectedItem is ComPairRow selectedPort)
        {
            await DeleteComPortRowAsync(selectedPort);
            return;
        }

        if (MappingsGrid.SelectedItem is not MappingRow row)
        {
            SetStatus(T("Status.SelectPairFirst"), "warn");
            return;
        }

        try
        {
            if (row.IsKmdf)
            {
                await DeleteKmdfPortForSelectedAsync(row);
                return;
            }

            CommitGridEdits();
            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            SetComPorts(pairs, await GetKmdfDevicesAsync());
            var pair = pairs.FirstOrDefault(p => PairMatchesMapping(p, row));
            if (pair is null)
            {
                DependenciesText.Text = FormatCom0comPairs(pairs);
                SetStatus(TF("Status.NoPairMatches", row.VisiblePort, row.BackingPort), "warn");
                return;
            }

            await DeleteCom0comPairAsync(pair);
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DeletePairFailed", ex.Message), "error");
        }
    }

    private async Task DeleteComPortRowAsync(ComPairRow row)
    {
        if (row.IsKmdf)
        {
            await DeleteKmdfPortAsync(row.ToKmdfDeviceInfo());
            return;
        }

        await DeleteCom0comPairAsync(row.ToInfo());
    }

    private async Task DeleteCom0comPairAsync(Com0comPairInfo pair)
    {
        try
        {
            var answer = MessageBox.Show(
                TF("Prompt.DeletePair", pair.PairNumber, PairPortText(pair.PortA, "Diag.MissingA"), PairPortText(pair.PortB, "Diag.MissingB")),
                T("Title.ComPair"),
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
                SetStatus(TF("Status.DeletePairFailed", ExtractError(body)), "error");
                return;
            }

            var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
            if (plan is null)
            {
                SetStatus(T("Status.DeletePairEmptyPlan"), "error");
                return;
            }

            LaunchSetupcPlan(plan);
            await RefreshComPairsListAsync(updateDetails: true);
            SetStatus(TF("Status.WaitingPairRemoved", pair.PairNumber));
            _ = PollCom0comPairsAfterSetupcAsync(removedPairNumber: pair.PairNumber);
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DeletePairFailed", ex.Message), "error");
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

        SetStatus(TF("Status.LaunchSetupc", plan.Arguments));
    }

    private async Task<bool> EnsurePairExistsBeforeStartAsync(MappingRow row)
    {
        if (row.IsKmdf)
        {
            return await EnsureKmdfPortExistsBeforeStartAsync(row);
        }

        var pairs = await RefreshComPairsListAsync(updateDetails: false);
        if (pairs.Any(pair => PairMatchesMapping(pair, row)))
        {
            return true;
        }

        DependenciesText.Text = FormatCom0comPairs(pairs);
        var answer = MessageBox.Show(
            TF("Prompt.MissingPair", row.VisiblePort, row.BackingPort),
            T("Title.ComPair"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetStatus(TF("Status.StartCanceledMissingPair", row.VisiblePort, row.BackingPort), "warn");
            return false;
        }

        var response = await _client.PostAsync($"/api/com0com/mappings/{row.Id}/create-plan", null);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            SetStatus(TF("Status.CreatePairFailed", ExtractError(body)), "error");
            return false;
        }

        var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
        if (plan is null)
        {
            SetStatus(T("Status.CreatePairEmptyPlan"), "error");
            return false;
        }

        LaunchSetupcPlan(plan);
        SetStatus(T("Status.WaitingPairAppear"));
        if (await WaitForPairAsync(row, TimeSpan.FromSeconds(45)))
        {
            SetStatus(TF("Status.CreatedPairStarting", row.VisiblePort, row.BackingPort));
            return true;
        }

        SetStatus(T("Status.PairCreationNotDetected"), "warn");
        return false;
    }

    private async Task CreateKmdfPortForSelectedAsync(MappingRow row)
    {
        CommitGridEdits();
        if (!await SaveMappingsAsync())
        {
            return;
        }

        var devices = await GetKmdfDevicesAsync();
        if (devices.Any(device => PortEquals(device.PortName, row.VisiblePort)))
        {
            SetStatus(TF("Status.KmdfPortExists", row.VisiblePort));
            DependenciesText.Text = FormatComPortInventory(await RefreshComPairsListAsync(updateDetails: false), devices);
            return;
        }

        var answer = MessageBox.Show(
            TF("Prompt.CreateKmdfPort", row.VisiblePort),
            T("Title.KmdfPort"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!LaunchKmdfCtl("add", row.VisiblePort))
        {
            return;
        }

        SetStatus(TF("Status.WaitingKmdfPortAppear", row.VisiblePort));
        if (await WaitForKmdfPortAsync(row.VisiblePort, shouldExist: true, TimeSpan.FromSeconds(60)))
        {
            SetStatus(TF("Status.CreatedKmdfPort", row.VisiblePort));
            return;
        }

        SetStatus(TF("Status.KmdfPortCreationNotDetected", row.VisiblePort), "warn");
    }

    private async Task DeleteKmdfPortForSelectedAsync(MappingRow row)
    {
        CommitGridEdits();
        var devices = await GetKmdfDevicesAsync();
        var device = devices.FirstOrDefault(candidate => PortEquals(candidate.PortName, row.VisiblePort));
        if (device is null)
        {
            DependenciesText.Text = FormatComPortInventory(await RefreshComPairsListAsync(updateDetails: false), devices);
            SetStatus(TF("Status.NoKmdfPortMatches", row.VisiblePort), "warn");
            return;
        }

        await DeleteKmdfPortAsync(device);
    }

    private async Task DeleteKmdfPortAsync(KmdfDeviceInfo device)
    {
        var answer = MessageBox.Show(
            TF("Prompt.DeleteKmdfPort", device.PortName, device.InstanceId),
            T("Title.KmdfPort"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!LaunchKmdfCtl("remove", device.PortName))
        {
            return;
        }

        SetStatus(TF("Status.WaitingKmdfPortRemoved", device.PortName));
        if (await WaitForKmdfPortAsync(device.PortName, shouldExist: false, TimeSpan.FromSeconds(60)))
        {
            SetStatus(TF("Status.DeletedKmdfPort", device.PortName));
            return;
        }

        SetStatus(TF("Status.DeleteKmdfPortStillListed", device.PortName), "warn");
    }

    private async Task<bool> EnsureKmdfPortExistsBeforeStartAsync(MappingRow row)
    {
        var devices = await GetKmdfDevicesAsync();
        if (devices.Any(device => PortEquals(device.PortName, row.VisiblePort)))
        {
            return true;
        }

        DependenciesText.Text = FormatComPortInventory(await RefreshComPairsListAsync(updateDetails: false), devices);
        var answer = MessageBox.Show(
            TF("Prompt.MissingKmdfPort", row.VisiblePort),
            T("Title.KmdfPort"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetStatus(TF("Status.StartCanceledMissingKmdfPort", row.VisiblePort), "warn");
            return false;
        }

        if (!LaunchKmdfCtl("add", row.VisiblePort))
        {
            return false;
        }

        SetStatus(TF("Status.WaitingKmdfPortAppear", row.VisiblePort));
        if (await WaitForKmdfPortAsync(row.VisiblePort, shouldExist: true, TimeSpan.FromSeconds(60)))
        {
            SetStatus(TF("Status.CreatedKmdfPortStarting", row.VisiblePort));
            return true;
        }

        SetStatus(TF("Status.KmdfPortCreationNotDetected", row.VisiblePort), "warn");
        return false;
    }

    private bool LaunchKmdfCtl(string action, string portName)
    {
        var cliPath = ResolveCliPath();
        if (cliPath is null)
        {
            SetStatus(T("Status.KmdfCliNotFound"), "error");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = $"kmdf {action} {portName}",
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            SetStatus(TF("Status.LaunchKmdfCtl", action, portName));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.LaunchKmdfCtlFailed", ex.Message), "error");
            return false;
        }
    }

    private async Task<bool> WaitForKmdfPortAsync(string portName, bool shouldExist, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                var devices = await GetKmdfDevicesAsync();
                var exists = devices.Any(device => PortEquals(device.PortName, portName));
                DependenciesText.Text = FormatComPortInventory(await RefreshComPairsListAsync(updateDetails: false), devices);
                if (exists == shouldExist)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                SetStatus(TF("Status.KmdfPortRefreshFailed", ex.Message), "warn");
                return false;
            }
        }

        return false;
    }

    private async Task<bool> WaitForPairAsync(MappingRow row, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var pairs = await RefreshComPairsListAsync(updateDetails: true);
            if (pairs.Any(pair => PairMatchesMapping(pair, row)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task PollCom0comPairsAfterSetupcAsync(int? removedPairNumber = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(removedPairNumber is null ? 5 : 45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(removedPairNumber is null ? 5 : 2));
            if (!await IsServiceReadyAsync())
            {
                return;
            }

            try
            {
                var pairs = await RefreshComPairsListAsync(updateDetails: true);
                if (removedPairNumber is null)
                {
                    return;
                }

                if (pairs.All(pair => pair.PairNumber != removedPairNumber.Value))
                {
                    SetStatus(TF("Status.DeletedPair", removedPairNumber.Value));
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus(TF("Status.PairRefreshFailed", ex.Message), "warn");
                return;
            }
        }

        if (removedPairNumber is not null)
        {
            SetStatus(TF("Status.DeleteStillListed", removedPairNumber.Value), "warn");
        }
    }

    private async Task<IReadOnlyList<Com0comPairInfo>> RefreshComPairsListAsync(bool updateDetails)
    {
        var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
        var devices = await GetKmdfDevicesAsync();
        SetComPorts(pairs, devices);
        if (updateDetails)
        {
            DependenciesText.Text = FormatComPortInventory(pairs, devices);
        }

        return pairs;
    }

    private async Task<IReadOnlyList<KmdfDeviceInfo>> GetKmdfDevicesAsync()
    {
        try
        {
            if (await IsServiceReadyAsync())
            {
                return await _client.GetFromJsonAsync<List<KmdfDeviceInfo>>("/api/kmdf/devices", JsonOptions) ?? [];
            }
        }
        catch
        {
        }

        return new KmdfDeviceManager().GetDevices();
    }

    private void SetComPorts(IReadOnlyList<Com0comPairInfo> pairs, IReadOnlyList<KmdfDeviceInfo> devices)
    {
        _comPairs.Clear();
        foreach (var pair in pairs)
        {
            _comPairs.Add(ComPairRow.From(pair, _language));
        }

        foreach (var device in devices)
        {
            _comPairs.Add(ComPairRow.From(device));
        }
    }

    private void ClearComPairsList() => _comPairs.Clear();

    private void RefreshComPortRowLabels()
    {
        foreach (var row in _comPairs)
        {
            row.RefreshLabels(_language);
        }
    }

    private void CommitGridEdits()
    {
        MappingsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        MappingsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private void NormalizeRowsBeforeSave()
    {
        foreach (var row in _mappings.Where(row => row.IsKmdf))
        {
            if (row.ClearBackingPort())
            {
                AppendGuiLog("info", T("Log.Gui"), TF("Log.KmdfBackingCleared", row.Name));
            }
        }

        foreach (var row in _mappings.Where(row => !row.IsKmdf))
        {
            if (string.Equals(row.VisiblePort, row.BackingPort, StringComparison.OrdinalIgnoreCase)
                && TryGetComPortNumber(row.VisiblePort, out var portNumber))
            {
                row.BackingPort = $"CNCB{portNumber}";
                AppendGuiLog("warn", T("Log.Gui"), TF("Log.BackingChanged", row.Name, row.BackingPort));
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
            ServiceStateText.Text = T("Status.ServiceChecking");
            var localReport = new DependencyDetector().Detect();
            DependenciesText.Text = FormatDependencies(localReport);
            if (localReport.IsReadyForCom0comHub4com)
            {
                SetStatus(T("Status.DependenciesAlreadyReady"));
                return;
            }

            DependencyInstallResult result;
            if (await IsServiceReadyAsync())
            {
                var response = await _client.PostAsJsonAsync("/api/dependencies/install", new DependencyInstallRequest(), JsonOptions);
                if (!response.IsSuccessStatusCode)
                {
                    SetStatus(TF("Status.DependencySetupFailed", await response.Content.ReadAsStringAsync()));
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
                ServiceStateText.Text = T("Status.DependenciesReady");
                AppendGuiLog("info", T("Log.Gui"), T("Log.DependenciesReady"));
                DependenciesText.Text += Environment.NewLine + FormatDependencies(refreshed);
                await RefreshAsync();
                return;
            }

            var installer = new DependencyInstaller(new DependencyDetector());
            if (installer.FindCom0comInstaller() is not null)
            {
                var answer = MessageBox.Show(
                    T("Prompt.LaunchCom0comInstaller"),
                    T("Title.DependencySetup"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    LaunchCom0comInstaller();
                    return;
                }
            }

            ServiceStateText.Text = T("Status.DependenciesDownloadedNeedInstall");
            AppendGuiLog("warn", T("Log.Gui"), T("Log.DependenciesDownloadedNeedInstall"));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DependencySetupFailed", ex.Message));
        }
    }

    private void LaunchCom0comInstaller()
    {
        try
        {
            var dependencyReport = new DependencyDetector().Detect();
            if (dependencyReport.IsReadyForCom0comHub4com)
            {
                SetStatus(T("Status.DependenciesAlreadyReady"));
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
            SetStatus(TF("Status.LaunchCom0comFailed", ex.Message));
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
                    SetStatus(T("Status.DependenciesReady"));
                    await RefreshAsync();
                    return;
                }

                ServiceStateText.Text = T("Status.WaitingInstaller");
            }

            SetStatus(T("Status.DependencyRefreshTimeout"));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DependencyRefreshFailed", ex.Message));
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
                    ServiceStateText.Text = TF("Status.ClearLogsStatusFailed", (int)response.StatusCode);
                    return;
                }
            }

            LogsText.Clear();
            ServiceStateText.Text = T("Status.LogsCleared");
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = TF("Status.ClearLogsFailed", ex.Message);
        }
    }

    private void SetStatus(string message, string level = "info")
    {
        ServiceStateText.Text = message;
        AppendGuiLog(level, T("Log.Gui"), message);
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

    private string FormatDependencies(SystemDependencyReport? report)
    {
        if (report is null)
        {
            return T("Diag.NoDependencyReport");
        }

        var builder = new StringBuilder();
        builder.AppendLine(TF("Diag.ComReady", report.IsReadyForCom0comHub4com));
        builder.AppendLine(TF("Diag.KmdfReady", report.IsReadyForKmdf));
        builder.AppendLine();
        foreach (var item in report.Items)
        {
            builder.AppendLine($"{(item.Found ? T("Diag.Found") : T("Diag.Missing"))} {item.Name}");
            builder.AppendLine(item.Path ?? item.Message);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string FormatInstallResult(DependencyInstallResult result)
    {
        var builder = new StringBuilder();
        foreach (var step in result.Steps)
        {
            builder.AppendLine($"{(step.Success ? T("Diag.Found") : T("Diag.Fail"))} {step.Name}");
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

    private string FormatCom0comPairs(IReadOnlyList<Com0comPairInfo> pairs)
    {
        var builder = new StringBuilder();
        builder.AppendLine(T("Diag.ComPairsTitle"));
        builder.AppendLine();
        if (pairs.Count == 0)
        {
            builder.AppendLine(T("Diag.NoComPairs"));
            return builder.ToString();
        }

        foreach (var pair in pairs)
        {
            var state = pair.IsComplete ? T("Diag.Found") : T("Diag.Partial");
            builder.AppendLine(TF("Diag.PairLine", state, pair.PairNumber, PairPortText(pair.PortA, "Diag.MissingA"), PairPortText(pair.PortB, "Diag.MissingB")));
            if (!string.IsNullOrWhiteSpace(pair.DeviceA))
            {
                builder.AppendLine($"  A: {pair.DeviceA}");
            }

            if (!string.IsNullOrWhiteSpace(pair.DeviceB))
            {
                builder.AppendLine($"  B: {pair.DeviceB}");
            }

            builder.AppendLine(TF("Diag.DeleteCommand", pair.PairNumber));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string FormatComPortInventory(IReadOnlyList<Com0comPairInfo> pairs, IReadOnlyList<KmdfDeviceInfo> devices)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormatCom0comPairs(pairs).TrimEnd());
        builder.AppendLine();
        builder.AppendLine(FormatKmdfDevices(devices).TrimEnd());
        return builder.ToString();
    }

    private string FormatKmdfDevices(IReadOnlyList<KmdfDeviceInfo> devices)
    {
        var builder = new StringBuilder();
        builder.AppendLine(T("Diag.KmdfPortsTitle"));
        builder.AppendLine();
        if (devices.Count == 0)
        {
            builder.AppendLine(T("Diag.NoKmdfPorts"));
            return builder.ToString();
        }

        foreach (var device in devices)
        {
            builder.AppendLine(TF("Diag.KmdfPortLine", device.PortName, device.Status, device.DriverName ?? ""));
            builder.AppendLine($"  {device.InstanceId}");
            if (!string.IsNullOrWhiteSpace(device.PortName))
            {
                builder.AppendLine($"  {TF("Diag.KmdfControl", KmdfTunnelSession.BuildControlDevicePath(device.PortName))}");
                builder.AppendLine($"  {TF("Diag.KmdfDeleteCommand", device.PortName)}");
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string PairPortText(string? port, string missingKey)
    {
        return string.IsNullOrWhiteSpace(port) ? T(missingKey) : port;
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

    private static bool PortEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatActionResult(string action, HttpResponseMessage response, string responseBody)
    {
        var actionLabel = ActionLabel(action);
        if (!response.IsSuccessStatusCode)
        {
            return $"{actionLabel}: {(int)response.StatusCode} {ExtractError(responseBody)}";
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

            return $"{actionLabel}: {state ?? response.StatusCode.ToString()}{suffix}";
        }
        catch
        {
            return $"{actionLabel}: {(int)response.StatusCode}";
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

public sealed class MappingRow : INotifyPropertyChanged
{
    private string _backend = "com0comHub4com";
    private string? _backingPort;
    private TunnelRunState _runState = TunnelRunState.Stopped;
    private string _stateLabel = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";

    public string Backend
    {
        get => _backend;
        set
        {
            var normalized = string.Equals(value, "kmdf", StringComparison.OrdinalIgnoreCase)
                ? "kmdf"
                : "com0comHub4com";
            if (string.Equals(_backend, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _backend = normalized;
            OnPropertyChanged(nameof(Backend));
            OnPropertyChanged(nameof(IsKmdf));

            if (IsKmdf && !string.IsNullOrWhiteSpace(_backingPort))
            {
                ClearBackingPort();
            }
        }
    }

    public string VisiblePort { get; set; } = "";

    public string? BackingPort
    {
        get => IsKmdf ? null : _backingPort;
        set
        {
            var normalized = IsKmdf ? null : value;
            SetBackingPort(normalized);
        }
    }

    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool AutoStart { get; set; }
    public bool RestartOnFailure { get; set; } = true;

    public TunnelRunState RunState
    {
        get => _runState;
        set
        {
            if (_runState == value)
            {
                return;
            }

            _runState = value;
            OnPropertyChanged(nameof(RunState));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public string StateLabel
    {
        get => _stateLabel;
        set
        {
            if (string.Equals(_stateLabel, value, StringComparison.Ordinal))
            {
                return;
            }

            _stateLabel = value;
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    public bool IsKmdf => string.Equals(Backend, "kmdf", StringComparison.OrdinalIgnoreCase);
    public bool CanStart => RunState is not TunnelRunState.Running and not TunnelRunState.Starting and not TunnelRunState.Unsupported;
    public bool CanStop => RunState is TunnelRunState.Running or TunnelRunState.Starting;

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
            RestartOnFailure = mapping.RestartOnFailure,
            RunState = TunnelRunState.Stopped
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

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public bool ClearBackingPort() => SetBackingPort(null);

    private bool SetBackingPort(string? value)
    {
        if (string.Equals(_backingPort, value, StringComparison.Ordinal))
        {
            return false;
        }

        _backingPort = value;
        OnPropertyChanged(nameof(BackingPort));
        return true;
    }
}

public sealed class ComPairRow
{
    public string Kind { get; set; } = "";
    public string Port { get; set; } = "";
    public string Details { get; set; } = "";
    public int PairNumber { get; set; }
    public string? PortA { get; set; }
    public string? PortB { get; set; }
    public string? DeviceA { get; set; }
    public string? DeviceB { get; set; }
    public string? InstanceId { get; set; }
    public string? DriverName { get; set; }
    public bool IsComplete { get; set; }
    public bool IsKmdf { get; set; }
    public string State { get; set; } = "";

    public static ComPairRow From(Com0comPairInfo pair, UiLanguage language)
    {
        var row = new ComPairRow
        {
            Kind = "com0com",
            Port = $"{pair.PortA ?? ""} <-> {pair.PortB ?? ""}".Trim(),
            Details = $"pair {pair.PairNumber}",
            PairNumber = pair.PairNumber,
            PortA = pair.PortA,
            PortB = pair.PortB,
            DeviceA = pair.DeviceA,
            DeviceB = pair.DeviceB,
            IsComplete = pair.IsComplete
        };
        row.RefreshLabels(language);
        return row;
    }

    public static ComPairRow From(KmdfDeviceInfo device)
    {
        return new ComPairRow
        {
            Kind = "kmdf",
            Port = device.PortName,
            Details = device.DriverName ?? device.InstanceId,
            InstanceId = device.InstanceId,
            DriverName = device.DriverName,
            IsComplete = device.IsStarted,
            IsKmdf = true,
            State = device.Status
        };
    }

    public void RefreshLabels(UiLanguage language)
    {
        if (!IsKmdf)
        {
            State = IsComplete ? GuiText.Get(language, "Diag.Found") : GuiText.Get(language, "Diag.Partial");
        }
    }

    public Com0comPairInfo ToInfo()
    {
        return new Com0comPairInfo(PairNumber, PortA, PortB, DeviceA, DeviceB, IsComplete);
    }

    public KmdfDeviceInfo ToKmdfDeviceInfo()
    {
        return new KmdfDeviceInfo(Port, InstanceId ?? "", State, DriverName, null, IsComplete);
    }
}
