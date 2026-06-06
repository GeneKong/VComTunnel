using System.Text.Json;
using System.Text.Json.Serialization;

namespace VComTunnel.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ConfigStore(string? path = null)
    {
        Path = path ?? AppPaths.ConfigPath;
    }

    public string Path { get; }

    public async Task<VComTunnelConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new VComTunnelConfig();
        }

        await using var stream = File.OpenRead(Path);
        var config = await JsonSerializer.DeserializeAsync<VComTunnelConfig>(stream, JsonOptions, cancellationToken);
        return config ?? new VComTunnelConfig();
    }

    public async Task SaveAsync(VComTunnelConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tempPath = Path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        if (File.Exists(Path))
        {
            File.Replace(tempPath, Path, null);
        }
        else
        {
            File.Move(tempPath, Path);
        }
    }
}
