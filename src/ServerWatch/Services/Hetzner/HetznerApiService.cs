using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Hetzner;

namespace ServerWatch.Services.Hetzner;

/// <summary>
/// Client for the Hetzner Cloud API (https://api.hetzner.cloud/v1). Bearer-token auth,
/// token read from <see cref="HetznerConfigService"/> per request so changes apply without restart.
/// </summary>
public class HetznerApiService : IHetznerService
{
    private const string BaseUrl = "https://api.hetzner.cloud";

    private readonly HttpClient _http;
    private readonly HetznerConfigService _config;
    private readonly ILogger<HetznerApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public HetznerApiService(HttpClient http, HetznerConfigService config, ILogger<HetznerApiService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    private void ConfigureClient()
    {
        var token = _config.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Hetzner-API-Token ist nicht konfiguriert.");

        _http.BaseAddress ??= new Uri(BaseUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<T> GetAsync<T>(string path) where T : new()
    {
        ConfigureClient();
        var response = await _http.GetAsync($"/v1{path}");
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task<T> PostAsync<T>(string path, object? body = null) where T : new()
    {
        ConfigureClient();
        var content = body != null
            ? new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            : null;
        var response = await _http.PostAsync($"/v1{path}", content);
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task DeleteAsync(string path)
    {
        ConfigureClient();
        var response = await _http.DeleteAsync($"/v1{path}");
        await EnsureSuccess(response);
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

    // ─────────────────────────── Connection ───────────────────────────

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            ConfigureClient();
            var response = await _http.GetAsync("/v1/servers?per_page=1");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hetzner connection test failed");
            return false;
        }
    }

    // ─────────────────────────── Servers ───────────────────────────

    public async Task<List<HetznerServer>> ListServersAsync()
    {
        var resp = await GetAsync<HetznerServersResponse>("/servers?per_page=50&sort=name");
        return resp.Servers;
    }

    public async Task<HetznerServer?> GetServerAsync(long id)
    {
        try
        {
            var resp = await GetAsync<HetznerServerResponse>($"/servers/{id}");
            return resp.Server;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ─────────────────────────── Power actions ───────────────────────────

    public async Task<HetznerAction?> PowerOnAsync(long id) => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/poweron")).Action;
    public async Task<HetznerAction?> ShutdownAsync(long id) => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/shutdown")).Action;
    public async Task<HetznerAction?> RebootAsync(long id) => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/reboot")).Action;
    public async Task<HetznerAction?> ResetAsync(long id) => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/reset")).Action;

    // ─────────────────────────── Rescue ───────────────────────────

    public async Task<HetznerActionResponse?> EnableRescueAsync(long id)
        => await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/enable_rescue", new { type = "linux64" });

    public async Task<HetznerAction?> DisableRescueAsync(long id)
        => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/disable_rescue")).Action;

    // ─────────────────────────── Snapshots / backups ───────────────────────────

    public async Task<HetznerActionResponse?> CreateSnapshotAsync(long id, string? description)
        => await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/create_image",
            new { description = string.IsNullOrWhiteSpace(description) ? null : description, type = "snapshot" });

    public async Task<List<HetznerImage>> ListSnapshotsAsync()
    {
        var resp = await GetAsync<HetznerImagesResponse>("/images?type=snapshot&per_page=50&sort=created:desc");
        return resp.Images;
    }

    public async Task DeleteImageAsync(long imageId) => await DeleteAsync($"/images/{imageId}");

    public async Task<HetznerAction?> EnableBackupsAsync(long id)
        => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/enable_backup")).Action;

    public async Task<HetznerAction?> DisableBackupsAsync(long id)
        => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/disable_backup")).Action;

    // ─────────────────────────── Resize ───────────────────────────

    public async Task<List<HetznerServerType>> ListServerTypesAsync()
    {
        var resp = await GetAsync<HetznerServerTypesResponse>("/server_types?per_page=100");
        return resp.ServerTypes;
    }

    public async Task<HetznerAction?> ChangeServerTypeAsync(long id, string serverType, bool upgradeDisk)
        => (await PostAsync<HetznerActionResponse>($"/servers/{id}/actions/change_type",
            new { server_type = serverType, upgrade_disk = upgradeDisk })).Action;

    // ─────────────────────────── Metrics ───────────────────────────

    public async Task<HetznerMetrics?> GetMetricsAsync(long id, string type, DateTime start, DateTime end, int? step = null)
    {
        var s = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var e = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var query = $"?type={Uri.EscapeDataString(type)}&start={Uri.EscapeDataString(s)}&end={Uri.EscapeDataString(e)}";
        if (step is > 0) query += $"&step={step}";
        var resp = await GetAsync<HetznerMetricsResponse>($"/servers/{id}/metrics{query}");
        return resp.Metrics;
    }

    // ─────────────────────────── Firewalls & pricing ───────────────────────────

    public async Task<List<HetznerFirewall>> ListFirewallsAsync()
    {
        var resp = await GetAsync<HetznerFirewallsResponse>("/firewalls?per_page=50");
        return resp.Firewalls;
    }

    public async Task<HetznerPricing?> GetPricingAsync()
    {
        var resp = await GetAsync<HetznerPricingResponse>("/pricing");
        return resp.Pricing;
    }
}
