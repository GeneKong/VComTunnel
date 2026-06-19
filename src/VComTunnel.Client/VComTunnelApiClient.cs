using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VComTunnel.Core;

namespace VComTunnel.Client;

public sealed class VComTunnelApiClient : IDisposable
{
    public const string DefaultServiceUrl = "http://127.0.0.1:44817";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public VComTunnelApiClient(string serviceUrl)
        : this(new HttpClient { BaseAddress = NormalizeBaseUri(serviceUrl) }, ownsClient: true)
    {
    }

    public VComTunnelApiClient(HttpClient client, bool ownsClient = false)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ServiceStatus>("/api/status", cancellationToken);

    public Task<SystemDependencyReport> GetDependenciesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SystemDependencyReport>("/api/dependencies", cancellationToken);

    public async Task<IReadOnlyList<TunnelMapping>> GetMappingsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<TunnelMapping>>("/api/mappings", cancellationToken);

    public async Task SaveMappingsAsync(IEnumerable<TunnelMapping> mappings, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PutAsJsonAsync("/api/mappings", mappings.ToList(), JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<TunnelStatus> StartMappingAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync<TunnelStatus>($"/api/mappings/{id}/start", null, cancellationToken);

    public Task<TunnelStatus> StopMappingAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync<TunnelStatus>($"/api/mappings/{id}/stop", null, cancellationToken);

    public async Task<IReadOnlyList<LogEntry>> GetLogsAsync(int max = 500, CancellationToken cancellationToken = default) =>
        await GetAsync<List<LogEntry>>($"/api/logs?max={max}", cancellationToken);

    public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync("/api/logs", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<Com0comPairInfo>> GetCom0comPairsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<Com0comPairInfo>>("/api/com0com/pairs", cancellationToken);

    public async Task<IReadOnlyList<KmdfDeviceInfo>> GetKmdfDevicesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<KmdfDeviceInfo>>("/api/kmdf/devices", cancellationToken);

    public Task<DependencyInstallResult> InstallDependenciesAsync(
        DependencyInstallRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<DependencyInstallResult>("/api/dependencies/install", request, cancellationToken);

    public static Uri NormalizeBaseUri(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid VComTunnel service URL: {serviceUrl}", nameof(serviceUrl));
        }

        return uri;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = body is null
            ? await _client.PostAsync(path, null, cancellationToken)
            : await _client.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new VComTunnelApiException(response.StatusCode, "The service returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new VComTunnelApiException(response.StatusCode, ExtractError(body), body);
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The VComTunnel service returned an error.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? body;
            }

            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                return errors.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }
}

public sealed class VComTunnelApiException : Exception
{
    public VComTunnelApiException(HttpStatusCode statusCode, string message, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}
