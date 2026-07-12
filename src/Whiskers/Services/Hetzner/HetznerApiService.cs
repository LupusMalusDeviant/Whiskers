using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Hetzner;

/// <summary>
/// Client for the Hetzner Cloud API (https://api.hetzner.cloud/v1). The token is supplied per call
/// (credentials are per-server), so each request carries its own Authorization header — no shared
/// mutable state on the HttpClient, safe for concurrent calls with different tokens. Every call takes a
/// CancellationToken (OPT-12), threaded through to the underlying HttpClient.SendAsync.
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

    private async Task<T> SendAsync<T>(string token, HttpMethod method, string path, object? body = null, CancellationToken ct = default) where T : new()
    {
        using var req = BuildRequest(token, method, path, body);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccess(resp);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return new T();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task SendAsync(string token, HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = BuildRequest(token, method, path, body);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccess(resp);
    }

    private static HttpRequestMessage BuildRequest(string token, HttpMethod method, string path, object? body)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Hetzner API token is not configured.");

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
            401 => "Hetzner API token is invalid or expired.",
            403 => "Hetzner API token has insufficient permissions (read-only token used for a write action?).",
            404 => $"Hetzner resource not found: {response.RequestMessage?.RequestUri?.PathAndQuery}",
            423 => "Resource is locked (another action is still running). Please try again.",
            429 => "Hetzner API rate limit reached (max. 3600/hour). Please try again later.",
            _ => $"Hetzner API error ({statusCode}): {body}"
        };

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    // Hetzner paginates lists at max 50/page. Fetch every page (page=1,2,…) until a short page, so an
    // account with >50 servers/snapshots/types is returned in full — a partial list would let the cloud
    // name-fallback resolver miss existing servers. Bounded by a sane page cap.
    private async Task<List<TItem>> ListAllPagesAsync<TResponse, TItem>(
        string token, string basePath, Func<TResponse, List<TItem>> select, CancellationToken ct = default) where TResponse : new()
    {
        const int perPage = 50;
        var all = new List<TItem>();
        for (var page = 1; page <= 100; page++)
        {
            var sep = basePath.Contains('?') ? "&" : "?";
            var resp = await SendAsync<TResponse>(token, HttpMethod.Get, $"{basePath}{sep}page={page}&per_page={perPage}", ct: ct);
            var items = select(resp);
            all.AddRange(items);
            if (items.Count < perPage) break;
        }
        return all;
    }

    public async Task<bool> TestConnectionAsync(string token, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<HetznerServersResponse>(token, HttpMethod.Get, "/servers?per_page=1", ct: ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hetzner connection test failed");
            return false;
        }
    }

    public Task<List<HetznerServer>> ListServersAsync(string token, CancellationToken ct = default)
        => ListAllPagesAsync<HetznerServersResponse, HetznerServer>(token, "/servers?sort=name", r => r.Servers, ct);

    public async Task<HetznerServer?> GetServerAsync(string token, long id, CancellationToken ct = default)
    {
        try
        {
            return (await SendAsync<HetznerServerResponse>(token, HttpMethod.Get, $"/servers/{id}", ct: ct)).Server;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<HetznerAction?> PowerOnAsync(string token, long id, CancellationToken ct = default) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/poweron", ct: ct)).Action;
    public async Task<HetznerAction?> ShutdownAsync(string token, long id, CancellationToken ct = default) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/shutdown", ct: ct)).Action;
    public async Task<HetznerAction?> RebootAsync(string token, long id, CancellationToken ct = default) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/reboot", ct: ct)).Action;
    public async Task<HetznerAction?> ResetAsync(string token, long id, CancellationToken ct = default) => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/reset", ct: ct)).Action;

    public async Task<HetznerActionResponse?> EnableRescueAsync(string token, long id, CancellationToken ct = default)
        => await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/enable_rescue", new { type = "linux64" }, ct);

    public async Task<HetznerAction?> DisableRescueAsync(string token, long id, CancellationToken ct = default)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/disable_rescue", ct: ct)).Action;

    public async Task<HetznerActionResponse?> CreateSnapshotAsync(string token, long id, string? description, CancellationToken ct = default)
        => await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/create_image",
            new { description = string.IsNullOrWhiteSpace(description) ? null : description, type = "snapshot" }, ct);

    public Task<List<HetznerImage>> ListSnapshotsAsync(string token, CancellationToken ct = default)
        => ListAllPagesAsync<HetznerImagesResponse, HetznerImage>(token, "/images?type=snapshot&sort=created:desc", r => r.Images, ct);

    public async Task<HetznerImage?> GetImageAsync(string token, long imageId, CancellationToken ct = default)
    {
        try
        {
            return (await SendAsync<HetznerImageResponse>(token, HttpMethod.Get, $"/images/{imageId}", ct: ct)).Image;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteImageAsync(string token, long imageId, CancellationToken ct = default)
        => await SendAsync(token, HttpMethod.Delete, $"/images/{imageId}", ct: ct);

    public async Task<HetznerAction?> EnableBackupsAsync(string token, long id, CancellationToken ct = default)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/enable_backup", ct: ct)).Action;

    public async Task<HetznerAction?> DisableBackupsAsync(string token, long id, CancellationToken ct = default)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/disable_backup", ct: ct)).Action;

    public Task<List<HetznerServerType>> ListServerTypesAsync(string token, CancellationToken ct = default)
        => ListAllPagesAsync<HetznerServerTypesResponse, HetznerServerType>(token, "/server_types", r => r.ServerTypes, ct);

    public async Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk, CancellationToken ct = default)
        => (await SendAsync<HetznerActionResponse>(token, HttpMethod.Post, $"/servers/{id}/actions/change_type",
            new { server_type = serverType, upgrade_disk = upgradeDisk }, ct)).Action;

    public async Task<HetznerMetrics?> GetMetricsAsync(string token, long id, string type, DateTime start, DateTime end, int? step = null, CancellationToken ct = default)
    {
        var s = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var e = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var query = $"?type={Uri.EscapeDataString(type)}&start={Uri.EscapeDataString(s)}&end={Uri.EscapeDataString(e)}";
        if (step is > 0) query += $"&step={step}";
        return (await SendAsync<HetznerMetricsResponse>(token, HttpMethod.Get, $"/servers/{id}/metrics{query}", ct: ct)).Metrics;
    }
}
