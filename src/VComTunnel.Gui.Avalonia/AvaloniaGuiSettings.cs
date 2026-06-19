using System.Text.Json;
using VComTunnel.Client;

namespace VComTunnel.Gui.Avalonia;

internal sealed record AvaloniaGuiSettings(string ServiceUrl);

internal static class AvaloniaGuiSettingsStore
{
    private const string EnvironmentVariable = "VCOMTUNNEL_SERVICE_URL";

    public static string LoadServiceUrl()
    {
        var environmentUrl = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            return environmentUrl;
        }

        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return VComTunnelApiClient.DefaultServiceUrl;
            }

            var settings = JsonSerializer.Deserialize<AvaloniaGuiSettings>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(settings?.ServiceUrl)
                ? VComTunnelApiClient.DefaultServiceUrl
                : settings.ServiceUrl;
        }
        catch
        {
            return VComTunnelApiClient.DefaultServiceUrl;
        }
    }

    public static void SaveServiceUrl(string serviceUrl)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AvaloniaGuiSettings(serviceUrl), new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string SettingsPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            return Path.Combine(root, "VComTunnel", "avalonia-gui.json");
        }
    }
}
