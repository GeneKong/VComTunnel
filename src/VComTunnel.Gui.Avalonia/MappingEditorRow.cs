using VComTunnel.Core;

namespace VComTunnel.Gui.Avalonia;

internal sealed class MappingEditorRow : ViewModelBase
{
    private string _id = Guid.NewGuid().ToString("n");
    private string _name = "New tunnel";
    private TunnelBackend _backend = TunnelBackend.Com0comService;
    private string _visiblePort = "COM25";
    private string? _backingPort = "CNCB25";
    private string _host = "127.0.0.1";
    private string _portText = "5000";
    private bool _autoStart;
    private bool _restartOnFailure = true;
    private TunnelRunState _runState = TunnelRunState.Stopped;
    private string? _lastError;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public TunnelBackend Backend
    {
        get => _backend;
        set
        {
            if (!SetProperty(ref _backend, value))
            {
                return;
            }

            if (value == TunnelBackend.Kmdf)
            {
                BackingPort = null;
            }

            OnPropertyChanged(nameof(AllowsBackingPort));
            OnPropertyChanged(nameof(BackendLabel));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string VisiblePort
    {
        get => _visiblePort;
        set
        {
            if (SetProperty(ref _visiblePort, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string? BackingPort
    {
        get => _backingPort;
        set
        {
            var next = Backend == TunnelBackend.Kmdf ? null : value;
            if (SetProperty(ref _backingPort, next))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
            {
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool RestartOnFailure
    {
        get => _restartOnFailure;
        set => SetProperty(ref _restartOnFailure, value);
    }

    public TunnelRunState RunState
    {
        get => _runState;
        private set
        {
            if (SetProperty(ref _runState, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public bool AllowsBackingPort => Backend != TunnelBackend.Kmdf;

    public string BackendLabel => Backend switch
    {
        TunnelBackend.Com0comHub4com => "com0com + hub4com",
        TunnelBackend.Com0comService => "com0com + service",
        TunnelBackend.Kmdf => "KMDF",
        _ => Backend.ToString()
    };

    public string Endpoint => $"{Host}:{PortText}";

    public string StateLabel => RunState.ToString().ToLowerInvariant();

    public string Summary => $"{VisiblePort} -> {Endpoint}";

    public bool CanStart => RunState is not TunnelRunState.Running and not TunnelRunState.Starting and not TunnelRunState.Unsupported;

    public bool CanStop => RunState is TunnelRunState.Running or TunnelRunState.Starting;

    public static MappingEditorRow From(TunnelMapping mapping, TunnelStatus? status = null)
    {
        var row = new MappingEditorRow
        {
            Id = mapping.Id,
            Name = mapping.Name,
            Backend = mapping.Backend,
            VisiblePort = mapping.VisiblePort,
            BackingPort = mapping.Backend == TunnelBackend.Kmdf ? null : mapping.BackingPort,
            Host = mapping.Host,
            PortText = mapping.Port.ToString(),
            AutoStart = mapping.AutoStart,
            RestartOnFailure = mapping.RestartOnFailure
        };
        row.ApplyStatus(status);
        return row;
    }

    public bool TryToMapping(out TunnelMapping mapping, out string error)
    {
        mapping = new TunnelMapping();
        error = "";

        if (!int.TryParse(PortText, out var port))
        {
            error = $"{Name}: port must be a number.";
            return false;
        }

        mapping = new TunnelMapping
        {
            Id = Id,
            Name = Name.Trim(),
            Backend = Backend,
            VisiblePort = VisiblePort.Trim(),
            BackingPort = Backend == TunnelBackend.Kmdf ? null : NullIfWhiteSpace(BackingPort),
            Host = Host.Trim(),
            Port = port,
            AutoStart = AutoStart,
            RestartOnFailure = RestartOnFailure
        };
        return true;
    }

    public void ApplyStatus(TunnelStatus? status)
    {
        RunState = status?.State ?? TunnelRunState.Stopped;
        LastError = status?.LastError;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
