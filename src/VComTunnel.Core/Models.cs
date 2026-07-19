using System.Text.Json.Serialization;

namespace VComTunnel.Core;

public enum TunnelBackend
{
    Com0comHub4com,
    Com0comService,
    Kmdf
}

public enum TunnelProtocol
{
    Rfc2217
}

public enum SerialTrafficLogFormat
{
    Hex,
    EscapedText,
    Text,
    RawBinary
}

public enum SerialTrafficLogMode
{
    InUse,
    Exclusive
}

public enum SerialControlLinePolicy
{
    Keep,
    Low,
    High
}

public enum TunnelRunState
{
    Stopped,
    Starting,
    Running,
    Faulted,
    Unsupported
}

public enum TunnelFaultKind
{
    MissingDependencies,
    MissingLocalCom,
    LocalComBusy,
    NetworkRefused,
    NetworkTimeout,
    NetworkUnreachable,
    DriverProtocol,
    StartupTimeout,
    UnsupportedBackend,
    ProcessExited,
    Unknown
}

public sealed record TunnelMapping
{
    public const int DefaultRfc2217Port = 2217;

    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string Name { get; init; } = "New tunnel";
    public TunnelBackend Backend { get; init; } = TunnelBackend.Com0comHub4com;
    public string VisiblePort { get; init; } = "COM12";
    public string? BackingPort { get; init; } = "CNCB12";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = DefaultRfc2217Port;
    public TunnelProtocol Protocol { get; init; } = TunnelProtocol.Rfc2217;
    public bool Hub4comForwardControlLines { get; init; }
    public bool AutoStart { get; init; }
    public bool RestartOnFailure { get; init; } = true;
    public bool WirelessSerialAutoDiscover { get; init; }
    public string? WirelessSerialMac { get; init; }
    public string? WirelessSerialDeviceId { get; init; }
    public SerialTrafficLogOptions TrafficLog { get; init; } = new();
    [JsonIgnore]
    public bool SuppressInitialControlLineSync { get; init; }
}

public sealed record SerialTrafficLogOptions
{
    public bool Enabled { get; init; }
    public SerialTrafficLogMode Mode { get; init; } = SerialTrafficLogMode.InUse;
    public bool CaptureRx { get; init; } = true;
    public bool CaptureTx { get; init; } = true;
    public SerialTrafficLogFormat Format { get; init; } = SerialTrafficLogFormat.Text;
    public bool IncludeTimestamp { get; init; }
    public int BaudRate { get; init; } = 115200;
    public SerialControlLinePolicy Dtr { get; init; } = SerialControlLinePolicy.Keep;
    public SerialControlLinePolicy Rts { get; init; } = SerialControlLinePolicy.Keep;
    public string? DirectoryPath { get; init; }
    public int MaxFileSizeMb { get; init; } = 10;
    public int MaxFiles { get; init; } = 20;
}

public sealed record VComTunnelConfig
{
    public int SchemaVersion { get; init; } = 1;
    public List<TunnelMapping> Mappings { get; init; } = [];
}

public sealed record DependencyStatus(
    string Name,
    bool Found,
    string? Path,
    string Message);

public sealed record SystemDependencyReport(
    IReadOnlyList<DependencyStatus> Items,
    bool IsReadyForCom0comHub4com,
    bool IsReadyForKmdf);

public sealed record TunnelStatus(
    string Id,
    TunnelRunState State,
    string Backend,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    string? LastError,
    TunnelFaultKind? FaultKind,
    string? FaultHint);

public sealed record ServiceStatus(
    DateTimeOffset StartedAt,
    string ConfigPath,
    IReadOnlyList<TunnelStatus> Tunnels);

public sealed record SerialTrafficLogStatus(
    string Id,
    bool Enabled,
    string ActivePath,
    SerialTrafficLogMode Mode,
    bool Running,
    string? LastError);

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message);

public sealed record DependencyInstallRequest(
    bool InstallHub4com = true,
    bool DownloadCom0com = true,
    bool Force = false);

public sealed record DependencyInstallStep(
    string Name,
    bool Success,
    string Message,
    string? Path);

public sealed record DependencyInstallResult(
    IReadOnlyList<DependencyInstallStep> Steps,
    SystemDependencyReport DependencyReport);

public sealed record Com0comPairInfo(
    int PairNumber,
    string? PortA,
    string? PortB,
    string? DeviceA,
    string? DeviceB,
    bool IsComplete);

public sealed record KmdfDeviceInfo(
    string PortName,
    string InstanceId,
    string Status,
    string? DriverName,
    string? ProblemCode,
    bool IsStarted);

public sealed record KmdfPortRequest(
    string PortName,
    string? InstanceId = null,
    string? InfPath = null);

public sealed record KmdfPortOperationResult(
    bool Success,
    string Message,
    KmdfDeviceInfo? Device,
    bool RebootRequired);

public sealed record SetupcCommandPlan(
    string FileName,
    string? WorkingDirectory,
    string Arguments,
    bool RequiresElevation,
    string Description);

public sealed record SetupcCommandRunResult(
    bool Ok,
    int? ExitCode,
    string? Error,
    string Description);

public sealed record WirelessSerialDeviceEndpoint(
    string Mac,
    string? DeviceId,
    string? Alias,
    string? Name,
    string? Product,
    string? Board,
    string? Firmware,
    string IpAddress,
    int? ServicePort,
    string? Mode,
    int? WifiRssi,
    bool? ConfigMode,
    int? Clients,
    DateTimeOffset LastSeenAt,
    string Source);

public sealed record WirelessSerialEndpoint(
    string Mac,
    string Host,
    int? Port,
    DateTimeOffset LastSeenAt);

public sealed record WirelessSerialEndpointUpdateRequest(
    string Mac,
    string IpAddress,
    int? ServicePort = null,
    string? DeviceId = null,
    string? Alias = null,
    string? Name = null,
    string? Product = null,
    string? Board = null,
    string? Firmware = null,
    string? Mode = null,
    int? WifiRssi = null,
    bool? ConfigMode = null,
    int? Clients = null,
    string? Source = null);

[JsonSerializable(typeof(VComTunnelConfig))]
[JsonSerializable(typeof(TunnelMapping))]
[JsonSerializable(typeof(List<TunnelMapping>))]
[JsonSerializable(typeof(KmdfPortRequest))]
[JsonSerializable(typeof(WirelessSerialDeviceEndpoint))]
[JsonSerializable(typeof(List<WirelessSerialDeviceEndpoint>))]
[JsonSerializable(typeof(WirelessSerialEndpointUpdateRequest))]
public partial class VComTunnelJsonContext : JsonSerializerContext;
