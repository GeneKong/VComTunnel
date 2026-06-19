namespace VComTunnel.Core;

public static class ServiceEndpoint
{
    public const string DefaultUrl = "http://127.0.0.1:44817";
    public const string EnvironmentVariable = "VCOMTUNNEL_SERVICE_URL";

    public static string GetBaseUrl()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return DefaultUrl;
        }

        return NormalizeLoopbackHttpUrl(configuredUrl);
    }

    public static Uri GetBaseUri() => new(GetBaseUrl());

    private static string NormalizeLoopbackHttpUrl(string configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must be an absolute HTTP loopback URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must use http.");
        }

        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must point to a loopback host.");
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must not include a path.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must not include a query or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
