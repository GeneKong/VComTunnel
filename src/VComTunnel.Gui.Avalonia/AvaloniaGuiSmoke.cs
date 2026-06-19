using System.Runtime.InteropServices;
using VComTunnel.Client;

namespace VComTunnel.Gui.Avalonia;

internal static class AvaloniaGuiSmoke
{
    public static int Run(TextWriter output, TextWriter error)
    {
        try
        {
            var serviceUrl = AvaloniaGuiSettingsStore.LoadServiceUrl();
            var normalized = VComTunnelApiClient.NormalizeBaseUri(serviceUrl);
            output.WriteLine("VComTunnel Avalonia GUI smoke OK");
            output.WriteLine($"OS: {RuntimeInformation.OSDescription}");
            output.WriteLine($"Architecture: {RuntimeInformation.OSArchitecture}");
            output.WriteLine($"ServiceUrl: {normalized}");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"VComTunnel Avalonia GUI smoke FAILED: {ex.Message}");
            return 1;
        }
    }
}
