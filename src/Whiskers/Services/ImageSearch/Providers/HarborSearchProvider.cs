using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Whiskers.Services.ImageSearch.Providers;

/// <summary>
/// Self-hosted Harbor provider (opt-in via ImageSearch:Harbor:BaseUrl). Uses Harbor's /search API
/// for discovery and the artifacts API for tags. Optional basic-auth for private projects.
/// </summary>
public class HarborSearchProvider : IImageSearchProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HarborSearchProvider> _logger;
    private readonly ImageSearchSettings.HarborOptions _opts;
    private readonly string? _baseUrl;     // normalized, no trailing slash
    private readonly string? _host;        // registry host for the pull reference

    public HarborSearchProvider(IHttpClientFactory httpFactory, IOptions<ImageSearchSettings> options, ILogger<HarborSearchProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _opts = options.Value.Harbor;

        if (!string.IsNullOrWhiteSpace(_opts.BaseUrl) && Uri.TryCreate(_opts.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            _baseUrl = uri.GetLeftPart(UriPartial.Authority);
            _host = uri.Authority;
        }
    }

    public string Id => "harbor";
    public string DisplayName => "Harbor";
    public bool IsEnabled => _baseUrl != null;
    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (_baseUrl == null || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v2.0/search?q={Uri.EscapeDataString(query)}");
            AddAuth(req);
            var http = _httpFactory.CreateClient();
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Harbor search returned {Status} for '{Query}'", resp.StatusCode, query);
                return [];
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("repository", out var repos) || repos.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<ImageSearchResult>();
            foreach (var r in repos.EnumerateArray().Take(Math.Clamp(limit, 1, 100)))
            {
                var name = r.TryGetProperty("repository_name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new ImageSearchResult
                {
                    RegistryId = Id,
                    RegistryName = DisplayName,
                    Name = name,
                    PullReference = $"{_host}/{name}",
                    Pulls = r.TryGetProperty("pull_count", out var p) && p.TryGetInt64(out var pc) ? pc : null,
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Harbor search failed for '{Query}'", query);
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string repository, int limit, CancellationToken ct)
    {
        if (_baseUrl == null || string.IsNullOrWhiteSpace(repository)) return [];
        try
        {
            // repository_name is "project/repo[/more]"; project is the first segment, the rest is the
            // repository reference which Harbor requires URL-encoded (slashes double-encoded).
            var slash = repository.IndexOf('/');
            if (slash <= 0) return [];
            var project = repository[..slash];
            var repoRef = Uri.EscapeDataString(repository[(slash + 1)..]).Replace("%2F", "%252F");

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_baseUrl}/api/v2.0/projects/{Uri.EscapeDataString(project)}/repositories/{repoRef}/artifacts?with_tag=true&page_size={Math.Clamp(limit, 1, 100)}&sort=-push_time");
            AddAuth(req);
            var http = _httpFactory.CreateClient();
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var tags = new List<string>();
            foreach (var artifact in doc.RootElement.EnumerateArray())
            {
                if (!artifact.TryGetProperty("tags", out var tagArr) || tagArr.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in tagArr.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name)) tags.Add(name!);
                }
            }
            return tags;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Harbor tag lookup failed for '{Repo}'", repository);
            return [];
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_opts.Username))
        {
            var raw = Encoding.UTF8.GetBytes($"{_opts.Username}:{_opts.Password}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
    }
}
