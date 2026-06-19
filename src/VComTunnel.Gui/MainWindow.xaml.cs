using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    private readonly HttpClient _client = new() { BaseAddress = new Uri(ServiceEndpoint.DefaultUrl) };
    private readonly ObservableCollection<MappingRow> _mappings = [];
    private readonly ObservableCollection<ComPairRow> _comPairs = [];
    private readonly List<string> _guiLogLines = [];
    private readonly HashSet<string> _autoStartPromptRows = new(StringComparer.OrdinalIgnoreCase);
    private bool _serviceStartAttempted;
    private bool _dependencyPollActive;
    private bool _serviceRestarting;
    private bool _dependencySetupActive;
    private bool _updatingLanguage;
    private bool _updatingSelection;
    private bool _savingMappings;
    private bool _suppressAutoSave;
    private CancellationTokenSource? _autoSaveCts;
    private UiLanguage _language = GuiText.DefaultLanguage;

    public MainWindow()
    {
        InitializeComponent();
        _mappings.CollectionChanged += Mappings_CollectionChanged;
        MappingsGrid.ItemsSource = _mappings;
        ComPairsList.ItemsSource = _comPairs;
        ApplyLocalization();
        UpdateMappingCommandState();
        Loaded += MainWindow_Loaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
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
        SetToolbarButton(RestartServiceButton, PackIconMaterialKind.Restart, "Action.RestartService");
        SetToolbarButton(AddButton, PackIconMaterialKind.Plus, "Action.Add");
        SetToolbarButton(SaveButton, PackIconMaterialKind.ContentSave, "Action.Save");
        SetToolbarButton(DeleteMappingButton, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetToolbarButton(StartButton, PackIconMaterialKind.Play, "Action.Start");
        SetToolbarButton(StopButton, PackIconMaterialKind.Stop, "Action.Stop");
        SetToolbarButton(SetupDepsButton, PackIconMaterialKind.Cog, "Action.SetupDeps");
        SetToolbarButton(ClearLogsButton, PackIconMaterialKind.Broom, "Action.ClearLogs");

        SetCommandMenuItem(RefreshMenuItem, PackIconMaterialKind.Refresh, "Action.Refresh");
        SetCommandMenuItem(InstallServiceMenuItem, PackIconMaterialKind.Download, "Action.InstallService");
        SetCommandMenuItem(UninstallServiceMenuItem, PackIconMaterialKind.DeleteOutline, "Action.UninstallService");
        SetCommandMenuItem(StartServiceMenuItem, PackIconMaterialKind.Play, "Action.StartService");
        SetCommandMenuItem(StopServiceMenuItem, PackIconMaterialKind.Stop, "Action.StopService");
        SetCommandMenuItem(RestartServiceMenuItem, PackIconMaterialKind.Restart, "Action.RestartService");
        SetCommandMenuItem(AddMenuItem, PackIconMaterialKind.Plus, "Action.Add");
        SetCommandMenuItem(SaveMenuItem, PackIconMaterialKind.ContentSave, "Action.Save");
        SetCommandMenuItem(DeleteMappingMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetCommandMenuItem(StartMappingCommandMenuItem, PackIconMaterialKind.Play, "Action.Start");
        SetCommandMenuItem(StopMappingCommandMenuItem, PackIconMaterialKind.Stop, "Action.Stop");
        SetCommandMenuItem(PortsMenuItem, PackIconMaterialKind.FormatListBulleted, "Action.Ports");
        SetCommandMenuItem(CreatePairMenuItem, PackIconMaterialKind.Link, "Action.CreatePair");
        SetCommandMenuItem(DeletePairMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeletePair");
        SetCommandMenuItem(UpdateKmdfDriverMenuItem, PackIconMaterialKind.Upload, "Action.UpdateKmdfDriver");
        SetCommandMenuItem(SetupDepsMenuItem, PackIconMaterialKind.Cog, "Action.SetupDeps");
        SetCommandMenuItem(ClearLogsMenuItem, PackIconMaterialKind.Broom, "Action.ClearLogs");
        SetCommandMenuItem(StartMappingMenuItem, PackIconMaterialKind.Play, "Action.Start");
        SetCommandMenuItem(StopMappingMenuItem, PackIconMaterialKind.Stop, "Action.Stop");
        SetCommandMenuItem(SaveSelectedMappingMenuItem, PackIconMaterialKind.ContentSave, "Action.Save");
        SetCommandMenuItem(DeleteSelectedMappingMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteMapping");
        SetCommandMenuItem(DeleteSelectedComPairMenuItem, PackIconMaterialKind.DeleteOutline, "Action.DeleteSelectedPair");

        TunnelMappingsGroupBox.Header = T("Group.Mappings");
        DependenciesGroupBox.Header = T("Group.DependenciesPairs");
        LogsGroupBox.Header = T("Group.Logs");
        DependencyProgressText.Text = T("Status.DependencySetupRunning");
        NameColumn.Header = T("Column.Name");
        BackendColumn.Header = T("Column.Backend");
        VisibleComColumn.Header = T("Column.VisibleCom");
        BackingColumn.Header = T("Column.Backing");
        HostColumn.Header = T("Column.Host");
        PortColumn.Header = T("Column.Port");
        AutoColumn.Header = T("Column.Auto");
        RestartColumn.Header = T("Column.Restart");
        MappingStateColumn.Header = T("Column.State");
        ServiceColumn.Header = T("Column.Service");
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
            row.RefreshServiceLabel(_language);
        }

        MappingsGrid.Items.Refresh();
        UpdateMappingCommandState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _serviceStartAttempted = false;
        await RefreshAsync();
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e) => await InstallServiceAsync();
    private async void UninstallService_Click(object sender, RoutedEventArgs e) => await UninstallServiceAsync();
    private async void StartService_Click(object sender, RoutedEventArgs e) => await StartServiceAndRefreshAsync(force: true);
    private async void StopService_Click(object sender, RoutedEventArgs e) => await StopServiceAsync(confirm: true);
    private async void RestartService_Click(object sender, RoutedEventArgs e) => await RestartServiceAsync();

    private async void SetupDeps_Click(object sender, RoutedEventArgs e) => await SetupDependenciesAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void Ports_Click(object sender, RoutedEventArgs e) => await ShowCom0comPairsAsync();
    private async void CreatePair_Click(object sender, RoutedEventArgs e) => await CreatePairForSelectedAsync();
    private async void DeletePair_Click(object sender, RoutedEventArgs e) => await DeletePairForSelectedAsync();
    private async void UpdateKmdfDriver_Click(object sender, RoutedEventArgs e) => await UpdateKmdfDriverForSelectionAsync(confirm: true);
    private async void DeleteMapping_Click(object sender, RoutedEventArgs e) => await DeleteSelectedMappingAsync();
    private async void ClearLogs_Click(object sender, RoutedEventArgs e) => await ClearLogsAsync();

    private async void StartSelectedMapping_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("start");
    private async void StopSelectedMapping_Click(object sender, RoutedEventArgs e) => await PostSelectedAsync("stop");

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.InitialRefreshFailed", ex.Message), "error");
        }
    }

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

    private void MappingsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => ScheduleAutoSave();

    private void Mappings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MappingRow row in e.OldItems)
            {
                row.AutoStartEnabledRequested -= MappingRow_AutoStartEnabledRequested;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MappingRow row in e.NewItems)
            {
                row.AutoStartEnabledRequested += MappingRow_AutoStartEnabledRequested;
            }
        }
    }

    private async void MappingRow_AutoStartEnabledRequested(object? sender, EventArgs e)
    {
        if (sender is not MappingRow row || _suppressAutoSave)
        {
            return;
        }

        await HandleAutoStartEnabledAsync(row);
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
        var canUpdateKmdf = row?.IsKmdf == true || port?.IsKmdf == true;
        var canSaveMappings = _mappings.Count > 0 && !_savingMappings;

        SaveButton.IsEnabled = canSaveMappings;
        SaveMenuItem.IsEnabled = canSaveMappings;
        SaveSelectedMappingMenuItem.IsEnabled = canSaveMappings;
        DeleteMappingButton.IsEnabled = canUseMapping;
        DeleteMappingMenuItem.IsEnabled = canUseMapping;
        DeleteSelectedMappingMenuItem.IsEnabled = canUseMapping;
        CreatePairMenuItem.IsEnabled = canUseMapping;
        DeletePairMenuItem.IsEnabled = canUsePort;
        UpdateKmdfDriverMenuItem.IsEnabled = canUpdateKmdf;
        StartButton.IsEnabled = canStart;
        StopButton.IsEnabled = canStop;
        StartMappingCommandMenuItem.IsEnabled = canStart;
        StopMappingCommandMenuItem.IsEnabled = canStop;
        StartMappingMenuItem.IsEnabled = canStart;
        StopMappingMenuItem.IsEnabled = canStop;
        SetupDepsButton.IsEnabled = !_dependencySetupActive;
        SetupDepsMenuItem.IsEnabled = !_dependencySetupActive;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var portNumber = NextDefaultComPortNumber();
        var row = new MappingRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Tunnel {_mappings.Count + 1}",
            Backend = "com0comHub4com",
            VisiblePort = $"COM{portNumber}",
            BackingPort = $"CNCB{portNumber}",
            Host = "127.0.0.1",
            Port = 5000,
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
        ScheduleAutoSave();
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
            _suppressAutoSave = true;
            try
            {
                _mappings.Clear();
                foreach (var mapping in mappings)
                {
                    _mappings.Add(MappingRow.From(mapping));
                }
            }
            finally
            {
                _suppressAutoSave = false;
            }

            var serviceStatus = await RefreshMappingStatesAsync();
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
                ServiceStateText.Text = FormatServiceSummary(serviceStatus, _mappings.Count);
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

    private async Task RestartServiceAsync()
    {
        if (_serviceRestarting)
        {
            return;
        }

        _serviceRestarting = true;
        try
        {
            _serviceStartAttempted = false;
            SetStatus(T("Status.ServiceRestarting"));

            var serviceWasReady = await IsServiceReadyAsync();
            var installedServiceKnown = await TryStopInstalledWindowsServiceAsync();
            if ((serviceWasReady || installedServiceKnown) && await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(10)))
            {
                AppendGuiLog("info", T("Log.Gui"), T("Status.ServiceStopped"));
            }

            if (await IsServiceReadyAsync())
            {
                var stoppedLocalProcess = StopLocalServiceProcesses();
                if (stoppedLocalProcess)
                {
                    await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(6));
                }
            }

            if (await IsServiceReadyAsync())
            {
                SetStatus(T("Status.ServiceStopNotConfirmed"), "warn");
                await RefreshAsync();
                return;
            }

            _serviceStartAttempted = false;
            await StartServiceAndRefreshAsync(force: true);
            if (await IsServiceReadyAsync())
            {
                SetStatus(T("Status.ServiceRestarted"));
            }
        }
        finally
        {
            _serviceRestarting = false;
        }
    }

    private async Task InstallServiceAsync()
    {
        var answer = MessageBox.Show(
            T("Prompt.InstallService"),
            T("Menu.Service"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await InstallServiceCoreAsync();
    }

    private async Task<bool> InstallServiceCoreAsync()
    {
        var servicePath = ResolveServicePath();
        if (servicePath is null)
        {
            SetStatus(T("Diag.ServiceNotFound"), "error");
            return false;
        }

        SetStatus(T("Status.ServiceInstalling"));
        if (!await RunServiceCtlElevatedAsync("install", [servicePath]))
        {
            return false;
        }

        await RunServiceCtlElevatedAsync("stop", [], reportFailure: false);
        StopLocalServiceProcesses();
        await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(8));

        if (await RunServiceCtlElevatedAsync("start", []))
        {
            if (await WaitForServiceAsync(TimeSpan.FromSeconds(12)))
            {
                await RefreshAsync();
                SetStatus(T("Status.ServiceInstalled"));
                return true;
            }
        }

        SetStatus(T("Status.ServiceInstalled"));
        return true;
    }

    private async Task HandleAutoStartEnabledAsync(MappingRow row)
    {
        if (!row.AutoStart || !_autoStartPromptRows.Add(row.Id))
        {
            return;
        }

        CancelPendingAutoSave();
        try
        {
            var servicePath = ResolveServicePath();
            var service = await GetInstalledWindowsServiceInfoAsync();
            var servicePathMatches = ServiceBinaryPathMatches(service.BinaryPath, servicePath);
            if (service.State != InstalledWindowsServiceState.NotInstalled && !servicePathMatches)
            {
                await HandleAutoStartWithMismatchedServiceAsync(row, service.BinaryPath, servicePath);
                return;
            }

            if (service.State == InstalledWindowsServiceState.Running)
            {
                ScheduleAutoSave();
                return;
            }

            if (service.State == InstalledWindowsServiceState.NotInstalled)
            {
                await HandleAutoStartWithoutInstalledServiceAsync(row);
                return;
            }

            await HandleAutoStartWithStoppedServiceAsync(row);
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.AutoStartServiceCheckFailed", ex.Message), "warn");
            ScheduleAutoSave();
        }
        finally
        {
            _autoStartPromptRows.Remove(row.Id);
        }
    }

    private async Task HandleAutoStartWithoutInstalledServiceAsync(MappingRow row)
    {
        var answer = MessageBox.Show(
            T("Prompt.AutoStartInstallService"),
            T("Title.AutoStart"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            RevertAutoStart(row);
            return;
        }

        if (answer == MessageBoxResult.No)
        {
            if (await SaveMappingsAsync(logSuccess: false))
            {
                SetStatus(T("Status.AutoStartSavedNeedsService"), "warn");
            }
            return;
        }

        if (!await SaveMappingsAsync(logSuccess: false))
        {
            return;
        }

        if (await InstallServiceCoreAsync())
        {
            SetStatus(T("Status.AutoStartServiceReady"));
        }
    }

    private async Task HandleAutoStartWithMismatchedServiceAsync(MappingRow row, string? currentServicePath, string? expectedServicePath)
    {
        var answer = MessageBox.Show(
            TF(
                "Prompt.AutoStartRepairService",
                string.IsNullOrWhiteSpace(currentServicePath) ? T("Msg.Unknown") : currentServicePath,
                string.IsNullOrWhiteSpace(expectedServicePath) ? T("Msg.Unknown") : expectedServicePath),
            T("Title.AutoStart"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel)
        {
            RevertAutoStart(row);
            return;
        }

        if (answer == MessageBoxResult.No)
        {
            if (await SaveMappingsAsync(logSuccess: false))
            {
                SetStatus(T("Status.AutoStartSavedNeedsServiceRepair"), "warn");
            }
            return;
        }

        if (!await SaveMappingsAsync(logSuccess: false))
        {
            return;
        }

        if (await InstallServiceCoreAsync())
        {
            SetStatus(T("Status.AutoStartServiceRepaired"));
        }
    }

    private async Task HandleAutoStartWithStoppedServiceAsync(MappingRow row)
    {
        var answer = MessageBox.Show(
            T("Prompt.AutoStartStartService"),
            T("Title.AutoStart"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            RevertAutoStart(row);
            return;
        }

        if (answer == MessageBoxResult.No)
        {
            if (await SaveMappingsAsync(logSuccess: false))
            {
                SetStatus(T("Status.AutoStartSavedServiceStopped"), "warn");
            }
            return;
        }

        if (!await SaveMappingsAsync(logSuccess: false))
        {
            return;
        }

        StopLocalServiceProcesses();
        await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(8));
        if (await StartInstalledWindowsServiceElevatedAsync())
        {
            SetStatus(T("Status.AutoStartServiceReady"));
        }
    }

    private void RevertAutoStart(MappingRow row)
    {
        _suppressAutoSave = true;
        try
        {
            row.AutoStart = false;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        MappingsGrid.Items.Refresh();
        SetStatus(TF("Status.AutoStartCanceled", row.Name), "warn");
    }

    private async Task<bool> StartInstalledWindowsServiceElevatedAsync()
    {
        SetStatus(T("Status.WindowsServiceStarting"));
        if (!await RunServiceCtlElevatedAsync("start", []))
        {
            return false;
        }

        if (await WaitForServiceAsync(TimeSpan.FromSeconds(12)))
        {
            await RefreshAsync();
            SetStatus(T("Status.BackgroundServiceReady"));
            return true;
        }

        SetStatus(T("Status.ServiceStartFailed"), "warn");
        return false;
    }

    private async Task UninstallServiceAsync()
    {
        var answer = MessageBox.Show(
            T("Prompt.UninstallService"),
            T("Menu.Service"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetStatus(T("Status.ServiceUninstalling"));
        await RunServiceCtlElevatedAsync("stop", []);
        await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(10));
        if (!await RunServiceCtlElevatedAsync("uninstall", []))
        {
            return;
        }

        SetStatus(T("Status.ServiceUninstalled"));
        ClearComPairsList();
    }

    private async Task StopServiceAsync(bool confirm)
    {
        if (confirm)
        {
            var answer = MessageBox.Show(
                T("Prompt.StopService"),
                T("Menu.Service"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetStatus(T("Status.ServiceStopping"));
        await TryStopInstalledWindowsServiceAsync();
        StopLocalServiceProcesses();
        if (await WaitForServiceOfflineAsync(TimeSpan.FromSeconds(10)))
        {
            SetStatus(T("Status.ServiceStopped"));
            ClearComPairsList();
            return;
        }

        SetStatus(T("Status.ServiceStopNotConfirmed"), "warn");
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

        if (await TryStartInstalledWindowsServiceAsync())
        {
            await RefreshAsync();
            SetStatus(T("Status.BackgroundServiceReady"));
            return;
        }

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
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = servicePath,
                ArgumentList = { "--console" },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(servicePath)!
            });
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
            SetStatus(T("Status.BackgroundServiceReady"));
            return;
        }

        ServiceStateText.Text = T("Status.ServiceOffline");
        ClearComPairsList();
        DependenciesText.Text = TF("Diag.StartedButNotReady", servicePath);
        DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
    }

    private async Task<bool> RunServiceCtlElevatedAsync(string action, IReadOnlyList<string> arguments, bool reportFailure = true)
    {
        var cliPath = ResolveCliPath();
        if (cliPath is null)
        {
            if (reportFailure)
            {
                SetStatus(T("Status.ServiceCliNotFound"), "error");
            }
            return false;
        }

        try
        {
            var argumentText = new StringBuilder($"service {action}");
            foreach (var argument in arguments)
            {
                argumentText.Append(' ');
                argumentText.Append(QuoteProcessArgument(argument));
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = argumentText.ToString(),
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null)
            {
                SetStatus(TF("Status.ServiceControlFailed", action, $"Could not start {cliPath}."), "error");
                return false;
            }

            SetStatus(TF("Status.ServiceControlLaunched", action));
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                return true;
            }

            if (reportFailure)
            {
                SetStatus(TF("Status.ServiceControlFailed", action, $"exit code {process.ExitCode}"), "error");
            }
            return false;
        }
        catch (Exception ex)
        {
            if (reportFailure)
            {
                SetStatus(TF("Status.ServiceControlFailed", action, ex.Message), "error");
            }
            return false;
        }
    }

    private async Task<InstalledWindowsServiceInfo> GetInstalledWindowsServiceInfoAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new InstalledWindowsServiceInfo(InstalledWindowsServiceState.NotInstalled, null);
        }

        var query = await RunProcessAsync("sc.exe", ["query", "VComTunnel"]);
        if (query.ExitCode != 0)
        {
            return new InstalledWindowsServiceInfo(InstalledWindowsServiceState.NotInstalled, null);
        }

        var text = $"{query.Output} {query.Error}";
        var state = InstalledWindowsServiceState.Installed;
        if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            state = InstalledWindowsServiceState.Running;
        }
        else if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            state = InstalledWindowsServiceState.Stopped;
        }

        var config = await RunProcessAsync("sc.exe", ["qc", "VComTunnel"]);
        var binaryPath = config.ExitCode == 0
            ? ExtractServiceBinaryPath(config.Output)
            : null;
        return new InstalledWindowsServiceInfo(state, binaryPath);
    }

    private static string? ExtractServiceBinaryPath(string scConfigOutput)
    {
        foreach (var line in scConfigOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var nameIndex = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0)
            {
                continue;
            }

            var valueIndex = line.IndexOf(':', nameIndex);
            if (valueIndex >= 0 && valueIndex + 1 < line.Length)
            {
                return line[(valueIndex + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool ServiceBinaryPathMatches(string? registeredPath, string? expectedPath)
    {
        var registeredExe = ExtractExecutablePath(registeredPath);
        var expectedExe = ExtractExecutablePath(expectedPath);
        if (string.IsNullOrWhiteSpace(registeredExe) || string.IsNullOrWhiteSpace(expectedExe))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registeredExe),
                Path.GetFullPath(expectedExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(registeredExe, expectedExe, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        var value = commandLine?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1 ? value[1..endQuote] : value.Trim('"');
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0
            ? value[..(exeIndex + 4)].Trim()
            : value;
    }

    private async Task<bool> TryStartInstalledWindowsServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var service = await GetInstalledWindowsServiceInfoAsync();
        if (service.State == InstalledWindowsServiceState.NotInstalled)
        {
            return false;
        }

        var expectedServicePath = ResolveServicePath();
        if (!ServiceBinaryPathMatches(service.BinaryPath, expectedServicePath))
        {
            AppendGuiLog(
                "warn",
                T("Log.Gui"),
                TF(
                    "Status.ServicePathMismatch",
                    string.IsNullOrWhiteSpace(service.BinaryPath) ? T("Msg.Unknown") : service.BinaryPath,
                    string.IsNullOrWhiteSpace(expectedServicePath) ? T("Msg.Unknown") : expectedServicePath));
            return false;
        }

        if (service.State != InstalledWindowsServiceState.Running)
        {
            ServiceStateText.Text = T("Status.WindowsServiceStarting");
            var start = await RunProcessAsync("sc.exe", ["start", "VComTunnel"]);
            if (start.ExitCode != 0 && !start.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                AppendGuiLog("warn", T("Log.Gui"), CollapseWhitespace(start.Output + " " + start.Error));
                return false;
            }
        }

        return await WaitForServiceAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<bool> TryStopInstalledWindowsServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var query = await RunProcessAsync("sc.exe", ["query", "VComTunnel"]);
        if (query.ExitCode != 0)
        {
            return false;
        }

        if (query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ServiceStateText.Text = T("Status.ServiceStopping");
        var stop = await RunProcessAsync("sc.exe", ["stop", "VComTunnel"]);
        if (stop.ExitCode != 0
            && !stop.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)
            && !stop.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            AppendGuiLog("warn", T("Log.Gui"), TF("Status.ServiceStopFailed", CollapseWhitespace(stop.Output + " " + stop.Error)));
        }

        return true;
    }

    private static async Task<ProcessRunResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessRunResult(1, "", $"Could not start {fileName}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessRunResult(process.ExitCode, await outputTask, await errorTask);
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

    private async Task<bool> WaitForServiceOfflineAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await IsServiceReadyAsync())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return !await IsServiceReadyAsync();
    }

    private bool StopLocalServiceProcesses()
    {
        var stoppedAny = false;
        var servicePath = ResolveServicePath();
        foreach (var process in Process.GetProcessesByName("VComTunnel.Service"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !IsExpectedServiceProcess(process, servicePath))
                {
                    continue;
                }

                try
                {
                    ServiceStateText.Text = T("Status.ServiceStopping");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    stoppedAny = true;
                    AppendGuiLog("info", T("Log.Gui"), TF("Status.ServiceProcessStopped", process.Id));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    AppendGuiLog("warn", T("Log.Gui"), TF("Status.ServiceProcessStopFailed", process.Id, ex.Message));
                }
            }
        }

        return stoppedAny;
    }

    private static bool IsExpectedServiceProcess(Process process, string? servicePath)
    {
        if (servicePath is null)
        {
            return true;
        }

        try
        {
            var processPath = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(processPath)
                || string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(servicePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ServiceStatus?> RefreshMappingStatesAsync()
    {
        var status = await _client.GetFromJsonAsync<ServiceStatus>("/api/status", JsonOptions);
        var stateById = status?.Tunnels.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, TunnelStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _mappings)
        {
            row.ApplyServiceStatus(
                stateById.TryGetValue(row.Id, out var tunnel) ? tunnel : null,
                _language);
        }

        MappingsGrid.Items.Refresh();
        UpdateMappingCommandState();
        return status;
    }

    private string FormatServiceSummary(ServiceStatus? status, int mappingCount)
    {
        if (status is null)
        {
            return TF("Status.ServiceConnected", mappingCount);
        }

        var running = status.Tunnels.Count(t => t.State is TunnelRunState.Starting or TunnelRunState.Running);
        var faulted = status.Tunnels.Count(t => t.State is TunnelRunState.Faulted);
        return running > 0 || faulted > 0
            ? TF("Status.ServiceConnectedDetailed", mappingCount, running, faulted)
            : TF("Status.ServiceConnected", mappingCount);
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
        return TF("Diag.Offline", ServiceEndpoint.DefaultUrl, ex.Message);
    }

    private static bool IsLocalServiceApiException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException
            || ex.InnerException is HttpRequestException;
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

        CommitGridEdits();
        var portTarget = await FindPortTargetForMappingAsync(row);
        var removePort = false;
        if (portTarget is not null)
        {
            var answerWithPort = MessageBox.Show(
                TF("Prompt.DeleteMappingWithPort", row.Name, row.VisiblePort, portTarget.Description),
                T("Title.Mapping"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (answerWithPort == MessageBoxResult.Cancel)
            {
                return;
            }

            removePort = answerWithPort == MessageBoxResult.Yes;
        }
        else
        {
            var answer = MessageBox.Show(
                TF("Prompt.DeleteMapping", row.Name, row.VisiblePort),
                T("Title.Mapping"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (!await RemoveMappingRowAsync(row))
        {
            return;
        }

        if (!removePort || portTarget is null)
        {
            await RefreshAsync();
            SetStatus(TF("Status.DeletedMapping", row.Name));
            return;
        }

        var portRemoved = await DeleteMappingPortTargetAsync(portTarget);
        await RefreshAsync();
        SetStatus(
            portRemoved
                ? TF("Status.DeletedMappingAndPort", row.Name)
                : TF("Status.DeletedMappingPortPending", row.Name),
            portRemoved ? "info" : "warn");
    }

    private async Task<bool> RemoveMappingRowAsync(MappingRow row)
    {
        try
        {
            _autoSaveCts?.Cancel();
            if (row.CanStop)
            {
                await _client.PostAsync($"/api/mappings/{row.Id}/stop", null);
            }

            _mappings.Remove(row);
            MappingsGrid.SelectedItem = null;
            return await SaveMappingsAsync(logSuccess: false);
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DeleteMappingFailed", ex.Message), "error");
            await RefreshAsync();
            return false;
        }
    }

    private async Task<MappingPortTarget?> FindPortTargetForMappingAsync(MappingRow row)
    {
        try
        {
            if (row.IsKmdf)
            {
                var devices = await GetKmdfDevicesAsync();
                var device = devices.FirstOrDefault(candidate => PortEquals(candidate.PortName, row.VisiblePort));
                SetComPorts(await RefreshComPairsListAsync(updateDetails: false), devices);
                return device is null
                    ? null
                    : MappingPortTarget.ForKmdf(device);
            }

            var pairs = await _client.GetFromJsonAsync<List<Com0comPairInfo>>("/api/com0com/pairs", JsonOptions) ?? [];
            SetComPorts(pairs, await GetKmdfDevicesAsync());
            var pair = pairs.FirstOrDefault(candidate => PairMatchesMapping(candidate, row));
            return pair is null
                ? null
                : MappingPortTarget.ForCom0com(pair, PairPortText(pair.PortA, "Diag.MissingA"), PairPortText(pair.PortB, "Diag.MissingB"));
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> DeleteMappingPortTargetAsync(MappingPortTarget target)
    {
        return target.KmdfDevice is not null
            ? await DeleteKmdfPortAsync(target.KmdfDevice, confirm: false)
            : await DeleteCom0comPairAsync(target.Com0comPair!, confirm: false);
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

    private async Task PostMappingAsync(MappingRow row, string action, bool allowKmdfDriverRepair = true)
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
            if (allowKmdfDriverRepair
                && string.Equals(action, "start", StringComparison.OrdinalIgnoreCase)
                && row.IsKmdf
                && TryReadTunnelLastError(responseBody, out var lastError)
                && IsKmdfProtocolMismatch(lastError))
            {
                await PromptUpdateKmdfDriverAfterProtocolFaultAsync(row, lastError);
            }
        }
        catch (Exception ex) when (IsLocalServiceApiException(ex))
        {
            SetStatus(TF("Status.LocalServiceApiUnavailable", actionLabel, ServiceEndpoint.DefaultUrl, ex.Message), "error");
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            ClearComPairsList();
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.ActionFailed", actionLabel, ex.Message));
        }
    }

    private async Task<bool> SaveMappingsAsync()
    {
        return await SaveMappingsAsync(logSuccess: true);
    }

    private async Task<bool> SaveMappingsAsync(bool logSuccess)
    {
        _savingMappings = true;
        UpdateMappingCommandState();
        try
        {
            CommitGridEdits();
            NormalizeRowsBeforeSave();
            if (!await EnsureServiceReadyForWriteAsync(T("Action.Save")))
            {
                return false;
            }

            var mappings = _mappings.Select(r => r.ToMapping()).ToList();
            var response = await _client.PutAsJsonAsync("/api/mappings", mappings, JsonOptions);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                if (logSuccess)
                {
                    AppendGuiLog("info", T("Log.Gui"), TF("Status.SavedMappings", _mappings.Count));
                }
                return true;
            }

            SetStatus(TF("Status.SaveFailed", body));
            return false;
        }
        catch (Exception ex) when (IsLocalServiceApiException(ex))
        {
            SetStatus(TF("Status.LocalServiceApiUnavailable", T("Action.Save"), ServiceEndpoint.DefaultUrl, ex.Message), "error");
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            ClearComPairsList();
            return false;
        }
        finally
        {
            _savingMappings = false;
            UpdateMappingCommandState();
        }
    }

    private async Task<bool> EnsureServiceReadyForWriteAsync(string actionLabel)
    {
        if (await IsServiceReadyAsync())
        {
            return true;
        }

        ServiceStateText.Text = T("Status.ServiceStarting");
        if (await TryStartInstalledWindowsServiceAsync())
        {
            return true;
        }

        var servicePath = ResolveServicePath();
        if (servicePath is null)
        {
            ServiceStateText.Text = T("Status.ServiceOffline");
            ClearComPairsList();
            DependenciesText.Text = T("Diag.ServiceNotFound");
            DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = servicePath,
                ArgumentList = { "--console" },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(servicePath)!
            });
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.LocalServiceApiUnavailable", actionLabel, ServiceEndpoint.DefaultUrl, ex.Message), "error");
            DependenciesText.Text = BuildOfflineMessage(ex) + Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
            ClearComPairsList();
            return false;
        }

        if (await WaitForServiceAsync(TimeSpan.FromSeconds(10)))
        {
            return true;
        }

        ServiceStateText.Text = T("Status.ServiceOffline");
        ClearComPairsList();
        DependenciesText.Text = TF("Diag.StartedButNotReady", servicePath);
        DependenciesText.Text += Environment.NewLine + Environment.NewLine + FormatDependencies(new DependencyDetector().Detect());
        return false;
    }

    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave || _savingMappings || _mappings.Count == 0)
        {
            return;
        }

        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;
        _ = AutoSaveAfterDelayAsync(token);
    }

    private void CancelPendingAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;
    }

    private async Task AutoSaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (await SaveMappingsAsync(logSuccess: false))
            {
                ServiceStateText.Text = T("Status.AutoSaved");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.SaveFailed", ex.Message), "warn");
        }
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

            var existingPairs = await RefreshComPairsListAsync(updateDetails: true);
            if (existingPairs.Any(pair => PairMatchesMapping(pair, row)))
            {
                SetStatus(TF("Status.Com0comPairAlreadyExists", row.VisiblePort, row.BackingPort), "warn");
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

            if (!await RunSetupcPlanAsync(plan, TimeSpan.FromSeconds(60)))
            {
                return;
            }

            SetStatus(T("Status.WaitingPairAppear"));
            if (await WaitForPairAsync(row, TimeSpan.FromSeconds(45)))
            {
                SetStatus(TF("Status.CreatedPair", row.VisiblePort, row.BackingPort));
                return;
            }

            SetStatus(T("Status.PairCreationNotDetected"), "warn");
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

    private async Task<bool> DeleteComPortRowAsync(ComPairRow row)
    {
        if (row.IsKmdf)
        {
            return await DeleteKmdfPortAsync(row.ToKmdfDeviceInfo());
        }

        return await DeleteCom0comPairAsync(row.ToInfo());
    }

    private async Task<bool> DeleteCom0comPairAsync(Com0comPairInfo pair, bool confirm = true)
    {
        try
        {
            if (confirm)
            {
                var answer = MessageBox.Show(
                    TF("Prompt.DeletePair", pair.PairNumber, PairPortText(pair.PortA, "Diag.MissingA"), PairPortText(pair.PortB, "Diag.MissingB")),
                    T("Title.ComPair"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return false;
                }
            }

            var response = await _client.PostAsync($"/api/com0com/pairs/{pair.PairNumber}/remove-plan", null);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus(TF("Status.DeletePairFailed", ExtractError(body)), "error");
                return false;
            }

            var plan = JsonSerializer.Deserialize<SetupcCommandPlan>(body, JsonOptions);
            if (plan is null)
            {
                SetStatus(T("Status.DeletePairEmptyPlan"), "error");
                return false;
            }

            if (!await RunSetupcPlanAsync(plan, TimeSpan.FromSeconds(60)))
            {
                return false;
            }

            await RefreshComPairsListAsync(updateDetails: true);
            SetStatus(TF("Status.WaitingPairRemoved", pair.PairNumber));
            _ = PollCom0comPairsAfterSetupcAsync(removedPairNumber: pair.PairNumber);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.DeletePairFailed", ex.Message), "error");
            return false;
        }
    }

    private async Task<bool> RunSetupcPlanAsync(SetupcCommandPlan plan, TimeSpan timeout)
    {
        try
        {
            SetStatus(TF("Status.SetupcStarting", plan.Arguments));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments,
                WorkingDirectory = plan.WorkingDirectory ?? Path.GetDirectoryName(plan.FileName) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = plan.RequiresElevation ? "runas" : ""
            });
            if (process is null)
            {
                SetStatus(T("Status.SetupcStartReturnedNull"), "error");
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus(TF("Status.SetupcTimedOut", plan.Arguments, (int)timeout.TotalSeconds), "warn");
                return false;
            }

            if (process.ExitCode != 0)
            {
                SetStatus(TF("Status.SetupcFailedExit", plan.Arguments, process.ExitCode), "error");
                return false;
            }

            SetStatus(TF("Status.SetupcCompleted", plan.Arguments));
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus(T("Status.SetupcCanceled"), "warn");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.SetupcLaunchFailed", ex.Message), "error");
            return false;
        }

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

        if (!await RunSetupcPlanAsync(plan, TimeSpan.FromSeconds(60)))
        {
            return false;
        }

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

        if (!ConfirmKmdfDriverOperation(TF("Prompt.CreateKmdfPort", row.VisiblePort)))
        {
            return;
        }

        var operation = LaunchKmdfCtl("add", row.VisiblePort);
        if (operation is null)
        {
            return;
        }

        SetStatus(TF("Status.WaitingKmdfPortAppear", row.VisiblePort));
        var wait = await WaitForKmdfPortAsync(row.VisiblePort, shouldExist: true, TimeSpan.FromSeconds(60), operation);
        if (wait.Success)
        {
            SetStatus(TF("Status.CreatedKmdfPort", row.VisiblePort));
            return;
        }

        SetStatus(wait.FailureMessage ?? TF("Status.KmdfPortCreationNotDetected", row.VisiblePort), "warn");
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

    private async Task<bool> DeleteKmdfPortAsync(KmdfDeviceInfo device, bool confirm = true)
    {
        if (confirm)
        {
            var answer = MessageBox.Show(
                TF("Prompt.DeleteKmdfPort", device.PortName, device.InstanceId),
                T("Title.KmdfPort"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        var operation = LaunchKmdfCtl("remove", device.PortName);
        if (operation is null)
        {
            return false;
        }

        SetStatus(TF("Status.WaitingKmdfPortRemoved", device.PortName));
        var wait = await WaitForKmdfPortAsync(device.PortName, shouldExist: false, TimeSpan.FromSeconds(60), operation);
        if (wait.Success)
        {
            SetStatus(TF("Status.DeletedKmdfPort", device.PortName));
            return true;
        }

        SetStatus(wait.FailureMessage ?? TF("Status.DeleteKmdfPortStillListed", device.PortName), "warn");
        return false;
    }

    private async Task UpdateKmdfDriverForSelectionAsync(bool confirm)
    {
        var portName = GetSelectedKmdfPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            SetStatus(T("Status.SelectKmdfPortFirst"), "warn");
            return;
        }

        await UpdateKmdfDriverAsync(portName, confirm);
    }

    private string? GetSelectedKmdfPortName()
    {
        if (MappingsGrid.SelectedItem is MappingRow { IsKmdf: true } row)
        {
            return row.VisiblePort;
        }

        if (ComPairsList.SelectedItem is ComPairRow { IsKmdf: true } port)
        {
            return port.Port;
        }

        return null;
    }

    private async Task PromptUpdateKmdfDriverAfterProtocolFaultAsync(MappingRow row, string lastError)
    {
        if (!ConfirmKmdfDriverOperation(TF("Prompt.UpdateKmdfDriverAfterFault", row.VisiblePort, lastError)))
        {
            return;
        }

        if (await UpdateKmdfDriverAsync(row.VisiblePort, confirm: false))
        {
            SetStatus(TF("Status.KmdfDriverUpdatedRetrying", row.VisiblePort));
            await PostMappingAsync(row, "start", allowKmdfDriverRepair: false);
        }
    }

    private async Task<bool> UpdateKmdfDriverAsync(string portName, bool confirm)
    {
        var normalizedPortName = KmdfDeviceManager.NormalizePortName(portName);
        if (confirm)
        {
            if (!ConfirmKmdfDriverOperation(TF("Prompt.UpdateKmdfDriver", normalizedPortName)))
            {
                return false;
            }
        }

        var operation = LaunchKmdfCtl("update", normalizedPortName);
        if (operation is null)
        {
            return false;
        }

        SetStatus(TF("Status.WaitingKmdfDriverUpdate", normalizedPortName));
        var result = await WaitForKmdfCtlResultAsync(operation, TimeSpan.FromSeconds(90));
        if (result is null)
        {
            SetStatus(T("Status.KmdfCtlNoResult"), "warn");
            return false;
        }

        if (!result.Success)
        {
            AppendGuiLog("error", T("Log.Gui"), result.Message);
            SetStatus(TF("Status.KmdfCtlOperationFailed", CollapseWhitespace(result.Message)), "error");
            return false;
        }

        AppendGuiLog("info", T("Log.Gui"), result.Message);
        await RefreshComPairsListAsync(updateDetails: true);
        SetStatus(TF("Status.KmdfDriverUpdated", normalizedPortName));
        return true;
    }

    private async Task<bool> EnsureKmdfPortExistsBeforeStartAsync(MappingRow row)
    {
        var devices = await GetKmdfDevicesAsync();
        if (devices.Any(device => PortEquals(device.PortName, row.VisiblePort)))
        {
            return true;
        }

        DependenciesText.Text = FormatComPortInventory(await RefreshComPairsListAsync(updateDetails: false), devices);
        if (!ConfirmKmdfDriverOperation(TF("Prompt.MissingKmdfPort", row.VisiblePort)))
        {
            SetStatus(TF("Status.StartCanceledMissingKmdfPort", row.VisiblePort), "warn");
            return false;
        }

        var operation = LaunchKmdfCtl("add", row.VisiblePort);
        if (operation is null)
        {
            return false;
        }

        SetStatus(TF("Status.WaitingKmdfPortAppear", row.VisiblePort));
        var wait = await WaitForKmdfPortAsync(row.VisiblePort, shouldExist: true, TimeSpan.FromSeconds(60), operation);
        if (wait.Success)
        {
            SetStatus(TF("Status.CreatedKmdfPortStarting", row.VisiblePort));
            return true;
        }

        SetStatus(wait.FailureMessage ?? TF("Status.KmdfPortCreationNotDetected", row.VisiblePort), "warn");
        return false;
    }

    private bool ConfirmKmdfDriverOperation(string operationPrompt)
    {
        var message = $"{operationPrompt.TrimEnd()}\r\n\r\n{T("Prompt.KmdfExperimentalDriverWarning")}";
        var answer = MessageBox.Show(
            message,
            T("Title.KmdfPort"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private KmdfCtlLaunch? LaunchKmdfCtl(string action, string portName)
    {
        var cliPath = ResolveCliPath();
        if (cliPath is null)
        {
            SetStatus(T("Status.KmdfCliNotFound"), "error");
            return null;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.OperationsDirectory);
            var resultFile = Path.Combine(
                AppPaths.OperationsDirectory,
                $"kmdf-{action}-{KmdfDeviceManager.NormalizePortName(portName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.txt");
            Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = $"kmdf {action} {portName} --result-file {QuoteProcessArgument(resultFile)}",
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            SetStatus(TF("Status.LaunchKmdfCtl", action, portName));
            return new KmdfCtlLaunch(resultFile);
        }
        catch (Exception ex)
        {
            SetStatus(TF("Status.LaunchKmdfCtlFailed", ex.Message), "error");
            return null;
        }
    }

    private async Task<KmdfWaitResult> WaitForKmdfPortAsync(
        string portName,
        bool shouldExist,
        TimeSpan timeout,
        KmdfCtlLaunch? operation = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastOperationMessage = null;
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
                    return new KmdfWaitResult(true, null);
                }

                if (operation is not null && TryReadKmdfCtlResult(operation.ResultFile, out var result))
                {
                    lastOperationMessage = result.Message;
                    if (!result.Success)
                    {
                        AppendGuiLog("error", T("Log.Gui"), result.Message);
                        return new KmdfWaitResult(false, TF("Status.KmdfCtlOperationFailed", CollapseWhitespace(result.Message)));
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus(TF("Status.KmdfPortRefreshFailed", ex.Message), "warn");
                return new KmdfWaitResult(false, TF("Status.KmdfPortRefreshFailed", ex.Message));
            }
        }

        if (operation is null)
        {
            return new KmdfWaitResult(false, null);
        }

        if (lastOperationMessage is null)
        {
            return new KmdfWaitResult(false, T("Status.KmdfCtlNoResult"));
        }

        return new KmdfWaitResult(false, TF("Status.KmdfCtlNoPortAfterSuccess", CollapseWhitespace(lastOperationMessage)));
    }

    private async Task<KmdfCtlResult?> WaitForKmdfCtlResultAsync(KmdfCtlLaunch operation, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (TryReadKmdfCtlResult(operation.ResultFile, out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static string QuoteProcessArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool TryReadKmdfCtlResult(string resultFile, out KmdfCtlResult result)
    {
        result = new KmdfCtlResult(false, "");
        if (!File.Exists(resultFile))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(resultFile).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var success = text.StartsWith("OK ", StringComparison.OrdinalIgnoreCase);
            result = new KmdfCtlResult(success, text);
            return success || text.StartsWith("FAIL ", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    private int NextDefaultComPortNumber()
    {
        var used = new HashSet<int>();
        foreach (var row in _mappings)
        {
            AddPortNumber(used, row.VisiblePort);
            AddPortNumber(used, row.BackingPort);
        }

        foreach (var port in new WindowsComPortInventory().GetRegisteredPortNames())
        {
            AddPortNumber(used, port);
        }

        for (var portNumber = 12; portNumber < 256; portNumber++)
        {
            if (!used.Contains(portNumber))
            {
                return portNumber;
            }
        }

        return 12 + _mappings.Count;
    }

    private static bool TryGetComPortNumber(string? port, out int portNumber)
    {
        portNumber = 0;
        return !string.IsNullOrWhiteSpace(port)
            && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(port[3..], out portNumber)
            && portNumber > 0;
    }

    private static void AddPortNumber(ISet<int> ports, string? port)
    {
        if (TryGetComPortNumber(port, out var portNumber))
        {
            ports.Add(portNumber);
        }
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
        if (_dependencySetupActive)
        {
            return;
        }

        try
        {
            SetDependencySetupActive(true);
            ServiceStateText.Text = T("Status.ServiceChecking");
            SetStatus(T("Status.DependencySetupRunning"));
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
        finally
        {
            if (!_dependencyPollActive)
            {
                SetDependencySetupActive(false);
            }
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
        SetDependencySetupActive(true);
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
            SetDependencySetupActive(false);
        }
    }

    private void SetDependencySetupActive(bool active)
    {
        _dependencySetupActive = active;
        DependencyProgressPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DependencyProgressBar.IsIndeterminate = active;
        DependencyProgressText.Text = active ? T("Status.DependencySetupRunning") : "";
        UpdateMappingCommandState();
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

    private static bool TryReadTunnelLastError(string responseBody, out string lastError)
    {
        lastError = "";
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("lastError", out var errorElement)
                && errorElement.ValueKind != JsonValueKind.Null)
            {
                lastError = errorElement.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(lastError);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsKmdfProtocolMismatch(string error) =>
        error.Contains("KMDF driver protocol", StringComparison.OrdinalIgnoreCase)
        && error.Contains("older than required", StringComparison.OrdinalIgnoreCase);

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
    private bool _autoStart;
    private TunnelRunState _runState = TunnelRunState.Stopped;
    private string _stateLabel = "";
    private string _serviceLabel = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AutoStartEnabledRequested;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";

    public string Backend
    {
        get => _backend;
        set
        {
            var normalized = string.Equals(value, "kmdf", StringComparison.OrdinalIgnoreCase)
                ? "kmdf"
                : string.Equals(value, "com0comService", StringComparison.OrdinalIgnoreCase)
                    ? "com0comService"
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
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_autoStart == value)
            {
                return;
            }

            var enabled = !_autoStart && value;
            _autoStart = value;
            OnPropertyChanged(nameof(AutoStart));
            if (enabled)
            {
                AutoStartEnabledRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    public bool RestartOnFailure { get; set; } = true;
    public int? ProcessId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string? LastError { get; private set; }

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

    public string ServiceLabel
    {
        get => _serviceLabel;
        private set
        {
            if (string.Equals(_serviceLabel, value, StringComparison.Ordinal))
            {
                return;
            }

            _serviceLabel = value;
            OnPropertyChanged(nameof(ServiceLabel));
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
            Backend = mapping.Backend switch
            {
                TunnelBackend.Kmdf => "kmdf",
                TunnelBackend.Com0comService => "com0comService",
                _ => "com0comHub4com"
            },
            VisiblePort = mapping.VisiblePort,
            BackingPort = mapping.BackingPort,
            Host = mapping.Host,
            Port = mapping.Port,
            AutoStart = mapping.AutoStart,
            RestartOnFailure = mapping.RestartOnFailure,
            RunState = TunnelRunState.Stopped
        };
    }

    public void ApplyServiceStatus(TunnelStatus? status, UiLanguage language)
    {
        ProcessId = status?.ProcessId;
        StartedAt = status?.StartedAt;
        LastError = status?.LastError;
        RunState = status?.State ?? TunnelRunState.Stopped;
        StateLabel = GuiText.State(language, RunState);
        RefreshServiceLabel(language);
    }

    public void RefreshServiceLabel(UiLanguage language)
    {
        ServiceLabel = RunState switch
        {
            TunnelRunState.Running when ProcessId is not null => GuiText.Format(language, "Mapping.ServiceRunningPid", ProcessId),
            TunnelRunState.Running => GuiText.Get(language, "Mapping.ServiceRunning"),
            TunnelRunState.Starting => GuiText.Get(language, "Mapping.ServiceStarting"),
            TunnelRunState.Faulted when !string.IsNullOrWhiteSpace(LastError) => GuiText.Format(language, "Mapping.ServiceFaulted", Shorten(CollapseWhitespace(LastError!), 160)),
            TunnelRunState.Faulted => GuiText.Get(language, "Mapping.ServiceFaultedUnknown"),
            TunnelRunState.Unsupported => GuiText.Get(language, "Mapping.ServiceUnsupported"),
            _ => ""
        };
    }

    public TunnelMapping ToMapping()
    {
        var backend = string.Equals(Backend, "kmdf", StringComparison.OrdinalIgnoreCase)
            ? TunnelBackend.Kmdf
            : string.Equals(Backend, "com0comService", StringComparison.OrdinalIgnoreCase)
                ? TunnelBackend.Com0comService
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

    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

internal sealed record KmdfCtlLaunch(string ResultFile);

internal sealed record KmdfCtlResult(bool Success, string Message);

internal sealed record KmdfWaitResult(bool Success, string? FailureMessage);

internal sealed record ProcessRunResult(int ExitCode, string Output, string Error);

internal sealed record InstalledWindowsServiceInfo(InstalledWindowsServiceState State, string? BinaryPath);

internal enum InstalledWindowsServiceState
{
    NotInstalled,
    Installed,
    Stopped,
    Running
}

internal sealed record MappingPortTarget(Com0comPairInfo? Com0comPair, KmdfDeviceInfo? KmdfDevice, string Description)
{
    public static MappingPortTarget ForCom0com(Com0comPairInfo pair, string portA, string portB) =>
        new(pair, null, $"{portA} <-> {portB}");

    public static MappingPortTarget ForKmdf(KmdfDeviceInfo device) =>
        new(null, device, $"{device.PortName} ({device.InstanceId})");
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
