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

public enum TunnelRunState
{
    Stopped,
    Starting,
    Running,
    Faulted,
    Unsupported
}

public sealed record TunnelMapping
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string Name { get; init; } = "New tunnel";
    public TunnelBackend Backend { get; init; } = TunnelBackend.Com0comHub4com;
    public string VisiblePort { get; init; } = "COM12";
    public string? BackingPort { get; init; } = "CNCB12";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5000;
    public TunnelProtocol Protocol { get; init; } = TunnelProtocol.Rfc2217;
    public bool Hub4comForwardControlLines { get; init; }
    public bool AutoStart { get; init; }
    public bool RestartOnFailure { get; init; } = true;
    [JsonIgnore]
    public bool SuppressInitialControlLineSync { get; init; }
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
    string? LastError);

public sealed record ServiceStatus(
    DateTimeOffset StartedAt,
    string ConfigPath,
    IReadOnlyList<TunnelStatus> Tunnels);

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

[JsonSerializable(typeof(VComTunnelConfig))]
[JsonSerializable(typeof(TunnelMapping))]
[JsonSerializable(typeof(List<TunnelMapping>))]
[JsonSerializable(typeof(KmdfPortRequest))]
public partial class VComTunnelJsonContext : JsonSerializerContext;
