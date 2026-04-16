using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Coolify;

namespace ServerWatch.Services.Coolify;

public class CoolifyApiService : ICoolifyService
{
    private readonly HttpClient _http;
    private readonly CoolifyConfigService _config;
    private readonly ILogger<CoolifyApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public CoolifyApiService(HttpClient http, CoolifyConfigService config, ILogger<CoolifyApiService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    private void ConfigureClient()
    {
        var settings = _config.GetSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            throw new InvalidOperationException("Coolify-API-URL ist nicht konfiguriert.");

        _http.BaseAddress = new Uri(settings.ApiUrl.TrimEnd('/'));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<T> GetAsync<T>(string path) where T : new()
    {
        ConfigureClient();
        var response = await _http.GetAsync($"/api/v1{path}");
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task<string> GetStringAsync(string path)
    {
        ConfigureClient();
        var response = await _http.GetAsync($"/api/v1{path}");
        await EnsureSuccess(response);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task PostAsync(string path, object? body = null)
    {
        ConfigureClient();
        var content = body != null
            ? new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            : null;
        var response = await _http.PostAsync($"/api/v1{path}", content);
        await EnsureSuccess(response);
    }

    private async Task<T> PostAsync<T>(string path, object? body = null) where T : new()
    {
        ConfigureClient();
        var content = body != null
            ? new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            : null;
        var response = await _http.PostAsync($"/api/v1{path}", content);
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private async Task PatchAsync(string path, object body)
    {
        ConfigureClient();
        var content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        var response = await _http.PatchAsync($"/api/v1{path}", content);
        await EnsureSuccess(response);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;

        var message = statusCode switch
        {
            401 => "Coolify-API-Token ist ungültig oder abgelaufen.",
            403 => "Coolify-API-Token hat keine ausreichenden Berechtigungen.",
            404 => $"Coolify-Ressource nicht gefunden: {response.RequestMessage?.RequestUri?.PathAndQuery}",
            _ => $"Coolify-API-Fehler ({statusCode}): {body}"
        };

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    // --- Connection Test ---

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            ConfigureClient();
            var response = await _http.GetAsync("/api/v1/servers");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coolify connection test failed");
            return false;
        }
    }

    // --- Applications ---

    public async Task<List<CoolifyApplication>> ListApplicationsAsync()
    {
        return await GetAsync<List<CoolifyApplication>>("/applications");
    }

    public async Task<CoolifyApplication?> GetApplicationAsync(string uuid)
    {
        try
        {
            return await GetAsync<CoolifyApplication>($"/applications/{uuid}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<CoolifyDeployment> DeployApplicationAsync(string uuid, bool force = false)
    {
        var query = force ? $"?uuid={uuid}&force=true" : $"?uuid={uuid}";
        var result = await GetAsync<CoolifyDeployResponse>($"/deploy{query}");
        return result.Deployments.FirstOrDefault() ?? new CoolifyDeployment { Message = result.Message ?? "Deployment gestartet" };
    }

    public async Task StartApplicationAsync(string uuid)
    {
        await PostAsync($"/applications/{uuid}/start");
    }

    public async Task StopApplicationAsync(string uuid)
    {
        await PostAsync($"/applications/{uuid}/stop");
    }

    public async Task RestartApplicationAsync(string uuid)
    {
        await PostAsync($"/applications/{uuid}/restart");
    }

    public async Task<string> GetApplicationLogsAsync(string uuid)
    {
        return await GetStringAsync($"/applications/{uuid}/logs");
    }

    // --- Environment Variables ---

    public async Task<List<CoolifyEnvironmentVar>> GetEnvVarsAsync(string appUuid)
    {
        return await GetAsync<List<CoolifyEnvironmentVar>>($"/applications/{appUuid}/environment-variables");
    }

    public async Task SetEnvVarAsync(string appUuid, string key, string value, bool isBuildTime = false)
    {
        await PostAsync($"/applications/{appUuid}/environment-variables", new
        {
            key,
            value,
            is_build_time = isBuildTime
        });
    }

    // --- Servers ---

    public async Task<List<CoolifyServer>> ListServersAsync()
    {
        return await GetAsync<List<CoolifyServer>>("/servers");
    }

    public async Task<CoolifyServer?> GetServerAsync(string uuid)
    {
        try
        {
            return await GetAsync<CoolifyServer>($"/servers/{uuid}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // --- Databases ---

    public async Task<List<CoolifyDatabase>> ListDatabasesAsync()
    {
        return await GetAsync<List<CoolifyDatabase>>("/databases");
    }

    public async Task StartDatabaseAsync(string uuid)
    {
        await PostAsync($"/databases/{uuid}/start");
    }

    public async Task StopDatabaseAsync(string uuid)
    {
        await PostAsync($"/databases/{uuid}/stop");
    }

    // --- Batch Deploy ---

    public async Task<List<CoolifyDeployment>> DeployByTagAsync(string tag, bool force = false)
    {
        var query = force ? $"?tag={tag}&force=true" : $"?tag={tag}";
        var result = await GetAsync<CoolifyDeployResponse>($"/deploy{query}");
        return result.Deployments;
    }
}
