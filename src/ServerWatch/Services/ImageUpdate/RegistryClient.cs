using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace ServerWatch.Services.ImageUpdate;

/// <summary>
/// Queries Docker Registry v2 API for remote image manifest digests.
/// Supports Docker Hub (registry-1.docker.io) and generic v2 registries.
/// </summary>
public class RegistryClient : IRegistryClient
{
    private readonly HttpClient _http;
    private readonly ILogger<RegistryClient> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public RegistryClient(HttpClient http, ILogger<RegistryClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Get the remote manifest digest for an image reference.
    /// Returns null if the image can't be resolved (private registry, auth required, etc.)
    /// </summary>
    public async Task<string?> GetRemoteDigestAsync(string imageRef)
    {
        if (_cache.TryGetValue($"digest:{imageRef}", out string? cached))
            return cached;

        try
        {
            var parsed = ParseImageReference(imageRef);
            if (parsed == null) return null;

            var (registry, repo, tag) = parsed.Value;

            var url = $"https://{registry}/v2/{repo}/manifests/{tag}";

            // Docker Hub: pre-fetch an anonymous pull token (its manifest endpoint always requires one).
            string? token = registry == "registry-1.docker.io" ? await GetDockerHubTokenAsync(repo) : null;

            var response = await SendManifestHeadAsync(url, token);

            // Registry-agnostic bearer flow: GHCR/Quay/LSCR (and Docker Hub if the pre-fetch missed) answer
            // 401 with a WWW-Authenticate: Bearer challenge. Fetch a token from its realm and retry once.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var bearer = response.Headers.WwwAuthenticate.FirstOrDefault(h =>
                    string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
                if (bearer?.Parameter is { } param)
                {
                    var challengeToken = await GetRegistryTokenAsync(ParseBearerChallenge(param));
                    if (challengeToken != null)
                    {
                        response.Dispose();
                        response = await SendManifestHeadAsync(url, challengeToken);
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Registry returned {StatusCode} for {Image}", response.StatusCode, imageRef);
                return null;
            }

            var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.FirstOrDefault()
                : null;

            if (digest != null)
                _cache.Set($"digest:{imageRef}", digest, CacheDuration);

            return digest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get remote digest for {Image}", imageRef);
            return null;
        }
    }

    /// <summary>
    /// Parse image reference into (registry, repository, tag).
    /// Examples:
    ///   "nginx" → ("registry-1.docker.io", "library/nginx", "latest")
    ///   "nginx:1.25" → ("registry-1.docker.io", "library/nginx", "1.25")
    ///   "ghcr.io/owner/repo:v1" → ("ghcr.io", "owner/repo", "v1")
    /// </summary>
    public static (string registry, string repo, string tag)? ParseImageReference(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
            return null;

        // Remove digest suffix if present (e.g., image@sha256:abc)
        var atIndex = imageRef.IndexOf('@');
        if (atIndex >= 0)
            imageRef = imageRef[..atIndex];

        string registry;
        string repoAndTag;

        // Check if first part contains a dot or colon (indicating a registry)
        var firstSlash = imageRef.IndexOf('/');
        if (firstSlash > 0)
        {
            var firstPart = imageRef[..firstSlash];
            if (firstPart.Contains('.') || firstPart.Contains(':'))
            {
                registry = firstPart;
                repoAndTag = imageRef[(firstSlash + 1)..];
            }
            else
            {
                // Docker Hub with username (e.g., "myuser/myimage:tag")
                registry = "registry-1.docker.io";
                repoAndTag = imageRef;
            }
        }
        else
        {
            // Official Docker Hub image (e.g., "nginx" or "nginx:1.25")
            registry = "registry-1.docker.io";
            repoAndTag = imageRef;
        }

        // Split repo:tag
        string repo;
        string tag;
        var colonIndex = repoAndTag.LastIndexOf(':');
        if (colonIndex > 0)
        {
            repo = repoAndTag[..colonIndex];
            tag = repoAndTag[(colonIndex + 1)..];
        }
        else
        {
            repo = repoAndTag;
            tag = "latest";
        }

        // Docker Hub official images need "library/" prefix
        if (registry == "registry-1.docker.io" && !repo.Contains('/'))
            repo = $"library/{repo}";

        return (registry, repo, tag);
    }

    private async Task<string?> GetDockerHubTokenAsync(string repo)
    {
        try
        {
            var url = $"https://auth.docker.io/token?service=registry.docker.io&scope=repository:{repo}:pull";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("token").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Docker Hub token for {Repo}", repo);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendManifestHeadAsync(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        // Accept manifest v2 and OCI index for multi-arch images.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.list.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.index.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request);
    }

    private static readonly Regex ChallengeRegex = new(@"(?<key>\w+)=""(?<val>[^""]*)""", RegexOptions.Compiled);

    /// <summary>Parses a <c>Bearer realm="…",service="…",scope="…"</c> WWW-Authenticate parameter into its
    /// key/value pairs (quotes stripped) — the registry-agnostic OCI token-flow challenge.</summary>
    public static Dictionary<string, string> ParseBearerChallenge(string parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(parameter)) return result;
        foreach (Match m in ChallengeRegex.Matches(parameter))
            result[m.Groups["key"].Value] = m.Groups["val"].Value;
        return result;
    }

    /// <summary>Fetches an anonymous pull token from a registry's auth realm (from the parsed challenge).
    /// Returns null on any failure so the caller degrades to "unknown" rather than a crash/false update.</summary>
    private async Task<string?> GetRegistryTokenAsync(IReadOnlyDictionary<string, string> challenge)
    {
        if (!challenge.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
            return null;
        try
        {
            var query = new List<string>();
            if (challenge.TryGetValue("service", out var service)) query.Add($"service={Uri.EscapeDataString(service)}");
            if (challenge.TryGetValue("scope", out var scope)) query.Add($"scope={Uri.EscapeDataString(scope)}");
            var url = query.Count > 0 ? $"{realm}?{string.Join("&", query)}" : realm;

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
            if (doc.RootElement.TryGetProperty("access_token", out var at)) return at.GetString();
            return null;
        }
        catch (Exception ex)
        {
            // Realm only — never log the token.
            _logger.LogDebug(ex, "Failed to fetch registry token from realm {Realm}", realm);
            return null;
        }
    }
}
