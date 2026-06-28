using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ServerWatch.Services.ImageSearch.Providers;

/// <summary>
/// GitHub Container Registry provider. GHCR exposes no anonymous full-text search, so this provider
/// is a resolver: the query is treated as an exact "owner/repo" reference, validated against the
/// Registry v2 API (anonymous pull token, public images only). Tags come from the v2 tags/list.
/// </summary>
public class GhcrSearchProvider : IImageSearchProvider
{
    private const string Host = "ghcr.io";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GhcrSearchProvider> _logger;
    private readonly bool _enabled;

    public GhcrSearchProvider(IHttpClientFactory httpFactory, IOptions<ImageSearchSettings> options, ILogger<GhcrSearchProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _enabled = options.Value.GhcrEnabled;
    }

    public string Id => "ghcr";
    public string DisplayName => "GitHub (GHCR)";
    public bool IsEnabled => _enabled;
    public bool SupportsSearch => false;

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var repo = NormalizeRepo(query);
        if (repo == null) return [];

        // Validate the reference exists/is public by listing its tags.
        var tags = await GetTagsAsync(repo, 1, ct);
        if (tags.Count == 0) return [];

        return
        [
            new ImageSearchResult
            {
                RegistryId = Id,
                RegistryName = DisplayName,
                Name = repo,
                PullReference = $"{Host}/{repo}",
                Description = "GHCR-Paket (exakte Referenz)",
            }
        ];
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string repository, int limit, CancellationToken ct)
    {
        var repo = NormalizeRepo(repository);
        if (repo == null) return [];
        try
        {
            var http = _httpFactory.CreateClient();

            var token = await GetPullTokenAsync(http, repo, ct);
            if (token == null) return [];

            var req = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/v2/{repo}/tags/list");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return [];

            // v2 tags/list is ascending; reverse so newer-looking tags surface first, then clamp.
            return tags.EnumerateArray()
                .Select(t => t.GetString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .Reverse()
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GHCR tag lookup failed for '{Repo}'", repository);
            return [];
        }
    }

    private async Task<string?> GetPullTokenAsync(HttpClient http, string repo, CancellationToken ct)
    {
        try
        {
            var url = $"https://{Host}/token?service={Host}&scope=repository:{repo}:pull";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GHCR token request failed for '{Repo}'", repo);
            return null;
        }
    }

    /// <summary>Normalize a user reference to "owner/repo": strips a leading "ghcr.io/" and any ":tag".</summary>
    private static string? NormalizeRepo(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var s = reference.Trim();
        if (s.StartsWith($"{Host}/", StringComparison.OrdinalIgnoreCase))
            s = s[(Host.Length + 1)..];

        // Drop a trailing tag/digest.
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        var slash = s.IndexOf('/');
        var colon = s.LastIndexOf(':');
        if (colon > slash && colon >= 0) s = s[..colon];

        s = s.Trim('/');
        // GHCR repos are always at least "owner/name".
        return s.Contains('/') ? s : null;
    }
}
