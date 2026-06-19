using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using VComTunnel.Client;
using VComTunnel.Core;

namespace VComTunnel.Gui.Avalonia;

internal sealed class MainWindowViewModel : ViewModelBase
{
    private readonly List<string> _guiLogLines = [];
    private MappingEditorRow? _selectedMapping;
    private string _serviceUrl = AvaloniaGuiSettingsStore.LoadServiceUrl();
    private string _serviceHeadline = "Service not checked";
    private string _serviceDetail = "Set the service URL, then refresh.";
    private string _statusText = "Ready";
    private string _dependenciesText = "";
    private string _portsText = "";
    private string _logsText = "";
    private bool _isBusy;

    public MainWindowViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        AddCommand = new AsyncRelayCommand(() =>
        {
            AddMapping();
            return Task.CompletedTask;
        }, () => !IsBusy);
        DeleteCommand = new AsyncRelayCommand(() =>
        {
            DeleteSelectedMapping();
            return Task.CompletedTask;
        }, () => !IsBusy && SelectedMapping is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartSelectedAsync, () => !IsBusy && SelectedMapping?.CanStart == true);
        StopCommand = new AsyncRelayCommand(StopSelectedAsync, () => !IsBusy && SelectedMapping?.CanStop == true);
        ApplyServiceUrlCommand = new AsyncRelayCommand(ApplyServiceUrlAsync, () => !IsBusy);
        ClearLogsCommand = new AsyncRelayCommand(ClearLogsAsync, () => !IsBusy);
    }

    public ObservableCollection<MappingEditorRow> Mappings { get; } = [];

    public IReadOnlyList<TunnelBackend> BackendOptions { get; } =
        Enum.GetValues<TunnelBackend>();

    public MappingEditorRow? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            if (_selectedMapping is not null)
            {
                _selectedMapping.PropertyChanged -= SelectedMapping_PropertyChanged;
            }

            if (!SetProperty(ref _selectedMapping, value))
            {
                return;
            }

            if (_selectedMapping is not null)
            {
                _selectedMapping.PropertyChanged += SelectedMapping_PropertyChanged;
            }

            OnPropertyChanged(nameof(HasSelectedMapping));
            OnPropertyChanged(nameof(SelectedMappingAllowsBackingPort));
            RaiseCommandStates();
        }
    }

    public string ServiceUrl
    {
        get => _serviceUrl;
        set => SetProperty(ref _serviceUrl, value);
    }

    public string ServiceHeadline
    {
        get => _serviceHeadline;
        set => SetProperty(ref _serviceHeadline, value);
    }

    public string ServiceDetail
    {
        get => _serviceDetail;
        set => SetProperty(ref _serviceDetail, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string DependenciesText
    {
        get => _dependenciesText;
        set => SetProperty(ref _dependenciesText, value);
    }

    public string PortsText
    {
        get => _portsText;
        set => SetProperty(ref _portsText, value);
    }

    public string LogsText
    {
        get => _logsText;
        set => SetProperty(ref _logsText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelectedMapping => SelectedMapping is not null;

    public bool SelectedMappingAllowsBackingPort => SelectedMapping?.AllowsBackingPort == true;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ApplyServiceUrlCommand { get; }

    public AsyncRelayCommand ClearLogsCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async () =>
    {
        ServiceHeadline = "Connecting...";
        ServiceDetail = ServiceUrl;

        try
        {
            using var api = CreateClient();
            var status = await api.GetStatusAsync();
            var mappings = await api.GetMappingsAsync();
            ApplyMappings(mappings, status);

            DependenciesText = FormatDependencies(await api.GetDependenciesAsync());
            PortsText = await FormatPortsAsync(api);
            await RefreshLogsCoreAsync(api);

            ServiceHeadline = "Service online";
            ServiceDetail = $"{status.Tunnels.Count} running/faulted tunnel(s), config: {status.ConfigPath}";
            StatusText = $"Loaded {Mappings.Count} mapping(s).";
        }
        catch (Exception ex)
        {
            ServiceHeadline = "Service offline";
            ServiceDetail = FormatException(ex);
            AppendGuiLog("error", "gui", $"Refresh failed: {FormatException(ex)}");
        }
    });

    private Task ApplyServiceUrlAsync() => RunBusyAsync(() =>
    {
        var normalized = VComTunnelApiClient.NormalizeBaseUri(ServiceUrl).ToString().TrimEnd('/');
        ServiceUrl = normalized;
        AvaloniaGuiSettingsStore.SaveServiceUrl(normalized);
        StatusText = $"Service URL set to {normalized}.";
        AppendGuiLog("info", "gui", StatusText);
        return Task.CompletedTask;
    });

    private Task SaveAsync() => RunBusyAsync(async () =>
    {
        using var api = CreateClient();
        await SaveCoreAsync(api, showSuccess: true);
    });

    private Task StartSelectedAsync() => RunBusyAsync(async () =>
    {
        if (SelectedMapping is null)
        {
            return;
        }

        using var api = CreateClient();
        await SaveCoreAsync(api, showSuccess: false);
        var status = await api.StartMappingAsync(SelectedMapping.Id);
        SelectedMapping.ApplyStatus(status);
        StatusText = $"Start: {SelectedMapping.StateLabel}";
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            StatusText += $" ({status.LastError})";
        }

        AppendGuiLog(status.State == TunnelRunState.Faulted ? "error" : "info", "gui", StatusText);
        await RefreshLogsCoreAsync(api);
        RaiseCommandStates();
    });

    private Task StopSelectedAsync() => RunBusyAsync(async () =>
    {
        if (SelectedMapping is null)
        {
            return;
        }

        using var api = CreateClient();
        var status = await api.StopMappingAsync(SelectedMapping.Id);
        SelectedMapping.ApplyStatus(status);
        StatusText = $"Stop: {SelectedMapping.StateLabel}";
        AppendGuiLog("info", "gui", StatusText);
        await RefreshLogsCoreAsync(api);
        RaiseCommandStates();
    });

    private Task ClearLogsAsync() => RunBusyAsync(async () =>
    {
        using var api = CreateClient();
        await api.ClearLogsAsync();
        _guiLogLines.Clear();
        LogsText = "";
        StatusText = "Logs cleared.";
    });

    private async Task SaveCoreAsync(VComTunnelApiClient api, bool showSuccess)
    {
        var mappings = new List<TunnelMapping>();
        foreach (var row in Mappings)
        {
            if (!row.TryToMapping(out var mapping, out var error))
            {
                throw new InvalidOperationException(error);
            }

            mappings.Add(mapping);
        }

        await api.SaveMappingsAsync(mappings);
        if (showSuccess)
        {
            StatusText = $"Saved {mappings.Count} mapping(s).";
            AppendGuiLog("info", "gui", StatusText);
        }
    }

    private void AddMapping()
    {
        var number = NextComNumber();
        var row = new MappingEditorRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = $"Tunnel {Mappings.Count + 1}",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = $"COM{number}",
            BackingPort = $"CNCB{number}",
            Host = "127.0.0.1",
            PortText = "5000",
            RestartOnFailure = true
        };
        Mappings.Add(row);
        SelectedMapping = row;
        StatusText = "Mapping added. Edit, then save.";
        AppendGuiLog("info", "gui", StatusText);
    }

    private void DeleteSelectedMapping()
    {
        if (SelectedMapping is null)
        {
            return;
        }

        var removed = SelectedMapping;
        var index = Mappings.IndexOf(removed);
        Mappings.Remove(removed);
        SelectedMapping = Mappings.Count == 0 ? null : Mappings[Math.Clamp(index, 0, Mappings.Count - 1)];
        StatusText = $"Removed mapping {removed.Name}. Save to persist.";
        AppendGuiLog("info", "gui", StatusText);
    }

    private void ApplyMappings(IReadOnlyList<TunnelMapping> mappings, ServiceStatus status)
    {
        var selectedId = SelectedMapping?.Id;
        var statuses = status.Tunnels.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        Mappings.Clear();
        foreach (var mapping in mappings)
        {
            statuses.TryGetValue(mapping.Id, out var tunnelStatus);
            Mappings.Add(MappingEditorRow.From(mapping, tunnelStatus));
        }

        SelectedMapping = Mappings.FirstOrDefault(m => string.Equals(m.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Mappings.FirstOrDefault();
    }

    private async Task<string> FormatPortsAsync(VComTunnelApiClient api)
    {
        var builder = new StringBuilder();
        try
        {
            var pairs = await api.GetCom0comPairsAsync();
            builder.AppendLine("com0com");
            if (pairs.Count == 0)
            {
                builder.AppendLine("  no pairs");
            }

            foreach (var pair in pairs)
            {
                builder.AppendLine($"  pair {pair.PairNumber}: {pair.PortA ?? "-"} <-> {pair.PortB ?? "-"}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"com0com: {FormatException(ex)}");
        }

        builder.AppendLine();
        try
        {
            var devices = await api.GetKmdfDevicesAsync();
            builder.AppendLine("KMDF");
            if (devices.Count == 0)
            {
                builder.AppendLine("  no devices");
            }

            foreach (var device in devices)
            {
                builder.AppendLine($"  {device.PortName}: {device.Status} {device.DriverName}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"KMDF: {FormatException(ex)}");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task RefreshLogsCoreAsync(VComTunnelApiClient api)
    {
        var logs = await api.GetLogsAsync();
        var lines = logs.Select(log => $"{log.Timestamp:HH:mm:ss} {log.Level,-5} {log.Source}: {log.Message}");
        LogsText = string.Join(Environment.NewLine, lines.Concat(_guiLogLines));
    }

    private string FormatDependencies(SystemDependencyReport report)
    {
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

        return builder.ToString().TrimEnd();
    }

    private int NextComNumber()
    {
        var used = Mappings.Select(m => ParseComNumber(m.VisiblePort)).Where(n => n > 0).ToHashSet();
        for (var i = 25; i < 256; i++)
        {
            if (!used.Contains(i))
            {
                return i;
            }
        }

        return 25 + Mappings.Count;
    }

    private static int ParseComNumber(string port)
    {
        if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(port[3..], out var number) ? number : -1;
    }

    private void SelectedMapping_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingEditorRow.Backend) or nameof(MappingEditorRow.RunState))
        {
            OnPropertyChanged(nameof(SelectedMappingAllowsBackingPort));
            RaiseCommandStates();
        }
    }

    private VComTunnelApiClient CreateClient() => new(ServiceUrl);

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText = FormatException(ex);
            AppendGuiLog("error", "gui", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendGuiLog(string level, string source, string message)
    {
        _guiLogLines.Add($"{DateTimeOffset.Now:HH:mm:ss} {level,-5} {source}: {message}");
        if (_guiLogLines.Count > 200)
        {
            _guiLogLines.RemoveRange(0, _guiLogLines.Count - 200);
        }

        LogsText = string.IsNullOrWhiteSpace(LogsText)
            ? _guiLogLines[^1]
            : LogsText + Environment.NewLine + _guiLogLines[^1];
    }

    private static string FormatException(Exception ex) =>
        ex is VComTunnelApiException apiException
            ? $"{(int)apiException.StatusCode} {apiException.Message}"
            : ex.Message;

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        AddCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ApplyServiceUrlCommand.RaiseCanExecuteChanged();
        ClearLogsCommand.RaiseCanExecuteChanged();
    }
}
