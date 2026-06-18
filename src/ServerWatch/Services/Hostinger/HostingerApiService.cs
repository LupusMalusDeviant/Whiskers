using System.Net.Http.Headers;
using System.Text.Json;
using ServerWatch.Models.Hostinger;

namespace ServerWatch.Services.Hostinger;

/// <summary>
/// Client for the Hostinger VPS API (https://developers.hostinger.com/api/vps/v1). Bearer token per
/// call (per-server credentials). List/get responses are parsed tolerantly: Hostinger sometimes
/// wraps payloads in a "data" envelope and sometimes returns them bare.
/// </summary>
public class HostingerApiService : IHostingerService
{
    private const string BaseUrl = "https://developers.hostinger.com";
    private const string Prefix = "/api/vps/v1";

    private readonly HttpClient _http;
    private readonly ILogger<HostingerApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public HostingerApiService(HttpClient http, ILogger<HostingerApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    private static HttpRequestMessage Build(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Hostinger-API-Token ist nicht konfiguriert.");

        var req = new HttpRequestMessage(method, $"{BaseUrl}{Prefix}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<string> SendRawAsync(string token, HttpMethod method, string path)
    {
        using var req = Build(token, method, path);
        using var resp = await _http.SendAsync(req);
        await EnsureSuccess(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task SendAsync(string token, HttpMethod method, string path)
    {
        using var req = Build(token, method, path);
        using var resp = await _http.SendAsync(req);
        await EnsureSuccess(resp);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;
        var message = statusCode switch
        {
            401 => "Hostinger-API-Token ist ungültig oder abgelaufen.",
            403 => "Hostinger-API-Token hat keine ausreichenden Berechtigungen.",
            404 => $"Hostinger-Ressource nicht gefunden: {response.RequestMessage?.RequestUri?.PathAndQuery}",
            429 => "Hostinger-API-Ratelimit erreicht. Bitte später erneut versuchen.",
            _ => $"Hostinger-API-Fehler ({statusCode}): {body}"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    /// <summary>Unwraps a {"data": ...} envelope if present, otherwise returns the root element.</summary>
    private static JsonElement Unwrap(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            return data;
        return root;
    }

    public async Task<bool> TestConnectionAsync(string token)
    {
        try
        {
            await ListVmsAsync(token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hostinger connection test failed");
            return false;
        }
    }

    public async Task<List<HostingerVm>> ListVmsAsync(string token)
    {
        var json = await SendRawAsync(token, HttpMethod.Get, "/virtual-machines");
        if (string.IsNullOrWhiteSpace(json)) return new();
        using var doc = JsonDocument.Parse(json);
        var el = Unwrap(doc);
        if (el.ValueKind != JsonValueKind.Array) return new();
        return JsonSerializer.Deserialize<List<HostingerVm>>(el.GetRawText(), JsonOptions) ?? new();
    }

    public async Task<HostingerVm?> GetVmAsync(string token, long id)
    {
        try
        {
            var json = await SendRawAsync(token, HttpMethod.Get, $"/virtual-machines/{id}");
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<HostingerVm>(Unwrap(doc).GetRawText(), JsonOptions);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task StartAsync(string token, long id) => SendAsync(token, HttpMethod.Post, $"/virtual-machines/{id}/start");
    public Task StopAsync(string token, long id) => SendAsync(token, HttpMethod.Post, $"/virtual-machines/{id}/stop");
    public Task RestartAsync(string token, long id) => SendAsync(token, HttpMethod.Post, $"/virtual-machines/{id}/restart");

    public async Task<HostingerSnapshot?> GetSnapshotAsync(string token, long id)
    {
        try
        {
            var json = await SendRawAsync(token, HttpMethod.Get, $"/virtual-machines/{id}/snapshot");
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<HostingerSnapshot>(Unwrap(doc).GetRawText(), JsonOptions);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task CreateSnapshotAsync(string token, long id) => SendAsync(token, HttpMethod.Post, $"/virtual-machines/{id}/snapshot");
    public Task DeleteSnapshotAsync(string token, long id) => SendAsync(token, HttpMethod.Delete, $"/virtual-machines/{id}/snapshot");

    public Task<string> GetMetricsRawAsync(string token, long id) => SendRawAsync(token, HttpMethod.Get, $"/virtual-machines/{id}/metrics");
}
