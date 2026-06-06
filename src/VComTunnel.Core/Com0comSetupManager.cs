namespace VComTunnel.Core;

public sealed class Com0comSetupManager
{
    private readonly ConfigStore _configStore;
    private readonly DependencyDetector _dependencyDetector;
    private readonly IComPortInventory _comPortInventory;

    public Com0comSetupManager(
        ConfigStore configStore,
        DependencyDetector dependencyDetector,
        IComPortInventory comPortInventory)
    {
        _configStore = configStore;
        _dependencyDetector = dependencyDetector;
        _comPortInventory = comPortInventory;
    }

    public IReadOnlyList<Com0comPairInfo> GetPairs() => _comPortInventory.GetCom0comPairs();

    public async Task<SetupcCommandPlan> BuildCreatePlanAsync(string mappingId, CancellationToken cancellationToken = default)
    {
        var mapping = await GetMappingAsync(mappingId, cancellationToken);
        if (mapping.Backend != TunnelBackend.Com0comHub4com)
        {
            throw new InvalidOperationException("Only com0comHub4com mappings can create com0com pairs.");
        }

        if (string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            throw new InvalidOperationException("backingPort is required for com0com pair creation.");
        }

        return BuildPlan(
            $"install PortName={mapping.VisiblePort} PortName={mapping.BackingPort}",
            $"Create com0com pair {mapping.VisiblePort} <-> {mapping.BackingPort}");
    }

    public SetupcCommandPlan BuildRemovePlan(int pairNumber)
    {
        if (pairNumber < 0)
        {
            throw new InvalidOperationException("pairNumber must be zero or greater.");
        }

        return BuildPlan($"remove {pairNumber}", $"Remove com0com pair {pairNumber}");
    }

    private async Task<TunnelMapping> GetMappingAsync(string mappingId, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        return config.Mappings.FirstOrDefault(m => string.Equals(m.Id, mappingId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mapping '{mappingId}' was not found.");
    }

    private SetupcCommandPlan BuildPlan(string arguments, string description)
    {
        var setupc = _dependencyDetector.FindSetupc()
            ?? throw new FileNotFoundException("com0com setupc.exe was not found.");

        return new SetupcCommandPlan(
            setupc,
            Path.GetDirectoryName(setupc),
            arguments,
            RequiresElevation: true,
            description);
    }
}
