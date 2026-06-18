using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Hetzner;

namespace ServerWatch.Services.Hetzner;

/// <summary>
/// Client for the Hetzner Cloud API (https://api.hetzner.cloud/v1). The token is supplied per call
/// (credentials are per-server), so each request carries its own Authorization header — no shared
/// mutable state on the HttpClient, safe for concurrent calls with different tokens.
/// </summary>
public class HetznerApiService : IHetznerService
{
    private const string BaseUrl = "https://api.hetzner.cloud";

    private readonly HttpClient _http;
    private readonly ILogger<HetznerApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public HetznerApiService(HttpClient http, ILogger<HetznerApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    private async Task<T> SendAsync<T>(string token, HttpMethod method, string path, object? body = null) where T : new()
    {
        using var req = BuildRequest(token, method, path, body);
        using var resp = await _http.SendAsync(req);
        await EnsureSuccess(resp);
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return new T();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task SendAsync(string token, HttpMethod method, string path, object? body = null)
    {
        using var req = BuildRequest(token, method, path, body);
        using var resp = await _http.SendAsync(req);
        await EnsureSuccess(resp);
    }

    private static HttpRequestMessage BuildRequest(string token, HttpMethod method, string path, object? body)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Hetzner-API-Token ist nicht konfiguriert.");

        var req = new HttpRequestMessage(method, $"{BaseUrl}/v1{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;

        var message = statusCode switch
        {
            401 => "Hetzner-API-Token ist ungültig oder abgelaufen.",
            403 => "Hetzner-API-Token hat keine ausreichenden Berechtigungen (read-only Token für Schreibaktion?).",
            404 => $"Hetzner-Ressource nicht gefunden: {response.RequestMessage?.RequestUri?.PathAndQuery}",
            423 => "Ressource ist gesperrt (eine andere Aktion läuft noch). Bitte erneut versuchen.",
            429 => "Hetzner-API-Ratelimit erreicht (max. 3600/Stunde). Bitte später erneut versuchen.",
            _ => $"Hetzner-API-Fehler ({statusCode}): {body}"
        };

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public async Task<bool> TestConnectionAsync(string token)
    {
        try
        {
            await SendAsync<HetznerServersResponse>(token, HttpMethod.Get, "/servers?per_page=1");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hetzner connection test failed");
            return false;
        }
    }

    public async Task<List<HetznerServer>> ListServersAsync(string token)
        => (await SendAsync<HetznerServersResponse>(token, HttpMethod.Get, "/servers?per_page=50&sort=name")).Servers;

    public async Task<HetznerServer?> GetServerAsync(string token, long id)
    {
        try
        {
            return (await SendAsync<HetznerServerResponse>(token, HttpMethod.Get, $"/servers/{id}")).Server;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<HetznerAction?> PowerOnAsync(string token, long id) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/poweron")).Action;
    public async Task<HetznerAction?> ShutdownAsync(string token, long id) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/shutdown")).Action;
    public async Task<HetznerAction?> RebootAsync(string token, long id) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/reboot")).Action;
    public async Task<HetznerAction?> ResetAsync(string token, long id) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/reset")).Action;

    public async Task<HetznerActionResponse?> EnableRescueAsync(string token, long id)
        => await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/enable_rescue", new { type = "linux64" });

    public async Task<HetznerAction?> DisableRescueAsync(string token, long id)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/disable_rescue")).Action;

    public async Task<HetznerActionResponse?> CreateSnapshotAsync(string token, long id, string? description)
        => await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/create_image",
            new { description = string.IsNullOrWhiteSpace(description) ? null : description, type = "snapshot" });

    public async Task<List<HetznerImage>> ListSnapshotsAsync(string token)
        => (await SendAsync<HetznerImagesResponse>(token, HttpMethod.Get, "/images?type=snapshot&per_page=50&sort=created:desc")).Images;

    public async Task DeleteImageAsync(string token, long imageId)
        => await SendAsync(token, HttpMethod.Delete, $"/images/{imageId}");

    public async Task<HetznerAction?> EnableBackupsAsync(string token, long id)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/enable_backup")).Action;

    public async Task<HetznerAction?> DisableBackupsAsync(string token, long id)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/disable_backup")).Action;

    public async Task<List<HetznerServerType>> ListServerTypesAsync(string token)
        => (await SendAsync<HetznerServerTypesResponse>(token, HttpMethod.Get, "/server_types?per_page=100")).ServerTypes;

    public async Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/change_type",
            new { server_type = serverType, upgrade_disk = upgradeDisk })).Action;

    public async Task<HetznerMetrics?> GetMetricsAsync(string token, long id, string type, DateTime start, DateTime end, int? step = null)
    {
        var s = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var e = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var query = $"?type={Uri.EscapeDataString(type)}&start={Uri.EscapeDataString(s)}&end={Uri.EscapeDataString(e)}";
        if (step is > 0) query += $"&step={step}";
        return (await SendAsync<HetznerMetricsResponse>(token, HttpMethod.Get, $"/servers/{id}/metrics{query}")).Metrics;
    }
}
