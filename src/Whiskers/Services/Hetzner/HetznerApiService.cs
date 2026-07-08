using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Hetzner;

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

    // Hetzner paginates lists at max 50/page. Fetch every page (page=1,2,…) until a short page, so an
    // account with >50 servers/snapshots/types is returned in full — a partial list would let the cloud
    // name-fallback resolver miss existing servers. Bounded by a sane page cap.
    private async Task<List<TItem>> ListAllPagesAsync<TResponse, TItem>(
        string token, string basePath, Func<TResponse, List<TItem>> select) where TResponse : new()
    {
        const int perPage = 50;
        var all = new List<TItem>();
        for (var page = 1; page <= 100; page++)
        {
            var sep = basePath.Contains('?') ? "&" : "?";
            var resp = await SendAsync<TResponse>(token, HttpMethod.Get, $"{basePath}{sep}page={page}&per_page={perPage}");
            var items = select(resp);
            all.AddRange(items);
            if (items.Count < perPage) break;
        }
        return all;
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

    public Task<List<HetznerServer>> ListServersAsync(string token)
        => ListAllPagesAsync<HetznerServersResponse, HetznerServer>(token, "/servers?sort=name", r => r.Servers);

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

    public Task<List<HetznerImage>> ListSnapshotsAsync(string token)
        => ListAllPagesAsync<HetznerImagesResponse, HetznerImage>(token, "/images?type=snapshot&sort=created:desc", r => r.Images);

    public async Task<HetznerImage?> GetImageAsync(string token, long imageId)
    {
        try
        {
            return (await SendAsync<HetznerImageResponse>(token, HttpMethod.Get, $"/images/{imageId}")).Image;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteImageAsync(string token, long imageId)
        => await SendAsync(token, HttpMethod.Delete, $"/images/{imageId}");

    public async Task<HetznerAction?> EnableBackupsAsync(string token, long id)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/enable_backup")).Action;

    public async Task<HetznerAction?> DisableBackupsAsync(string token, long id)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/disable_backup")).Action;

    public Task<List<HetznerServerType>> ListServerTypesAsync(string token)
        => ListAllPagesAsync<HetznerServerTypesResponse, HetznerServerType>(token, "/server_types", r => r.ServerTypes);

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
