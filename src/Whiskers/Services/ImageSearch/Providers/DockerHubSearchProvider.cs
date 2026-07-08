using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Whiskers.Services.ImageSearch.Providers;

/// <summary>
/// Docker Hub provider. Uses Hub's public search API for discovery and the repositories/tags API
/// for the tag picker. Anonymous access (public images only).
/// </summary>
public class DockerHubSearchProvider : IImageSearchProvider
{
    private const string SearchApi = "https://hub.docker.com/v2/search/repositories/";
    private const string ReposApi = "https://hub.docker.com/v2/repositories/";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DockerHubSearchProvider> _logger;
    private readonly bool _enabled;

    public DockerHubSearchProvider(IHttpClientFactory httpFactory, IOptions<ImageSearchSettings> options, ILogger<DockerHubSearchProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _enabled = options.Value.DockerHubEnabled;
    }

    public string Id => "dockerhub";
    public string DisplayName => "Docker Hub";
    public bool IsEnabled => _enabled;
    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var http = _httpFactory.CreateClient();
            var url = $"{SearchApi}?query={Uri.EscapeDataString(query)}&page_size={Math.Clamp(limit, 1, 100)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Docker Hub search returned {Status} for '{Query}'", resp.StatusCode, query);
                return [];
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<ImageSearchResult>();
            foreach (var r in results.EnumerateArray())
            {
                var repoName = GetString(r, "repo_name");
                if (string.IsNullOrWhiteSpace(repoName)) continue;
                var isOfficial = GetBool(r, "is_official");

                list.Add(new ImageSearchResult
                {
                    RegistryId = Id,
                    RegistryName = DisplayName,
                    // Official images are stored as "library/<name>" but pulled as just "<name>".
                    Name = repoName,
                    PullReference = repoName,
                    Description = GetString(r, "short_description"),
                    Stars = GetLong(r, "star_count"),
                    Pulls = GetLong(r, "pull_count"),
                    IsOfficial = isOfficial,
                    IsVerified = isOfficial,
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker Hub search failed for '{Query}'", query);
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string repository, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository)) return [];
        try
        {
            // Official images live under the "library/" namespace on the tags API.
            var repoPath = repository.Contains('/') ? repository : $"library/{repository}";
            var http = _httpFactory.CreateClient();
            var url = $"{ReposApi}{repoPath}/tags/?page_size={Math.Clamp(limit, 1, 100)}&ordering=last_updated";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            return results.EnumerateArray()
                .Select(t => GetString(t, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker Hub tag lookup failed for '{Repo}'", repository);
            return [];
        }
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static long? GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
