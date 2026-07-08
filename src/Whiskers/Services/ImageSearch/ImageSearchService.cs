namespace Whiskers.Services.ImageSearch;

/// <summary>
/// Aggregates the registered <see cref="IImageSearchProvider"/>s. Searching with a registryId
/// targets one marketplace; searching with null fans out to every search-capable provider in
/// parallel and merges the results, ordered by popularity.
/// </summary>
public class ImageSearchService : IImageSearchService
{
    private readonly IReadOnlyList<IImageSearchProvider> _providers;
    private readonly ILogger<ImageSearchService> _logger;

    public ImageSearchService(IEnumerable<IImageSearchProvider> providers, ILogger<ImageSearchService> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public IReadOnlyList<ImageRegistryInfo> GetRegistries() =>
        _providers.Where(p => p.IsEnabled)
                  .Select(p => new ImageRegistryInfo(p.Id, p.DisplayName, p.SupportsSearch))
                  .ToList();

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, string? registryId, int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        if (!string.IsNullOrWhiteSpace(registryId))
        {
            var provider = _providers.FirstOrDefault(p => p.IsEnabled && p.Id == registryId);
            return provider == null ? [] : await provider.SearchAsync(query.Trim(), limit, ct);
        }

        // All-marketplaces: only providers that actually support discovery (skip exact-resolve ones
        // like GHCR to avoid surprising "no results" for a free-text query).
        var searchable = _providers.Where(p => p.IsEnabled && p.SupportsSearch).ToList();
        var tasks = searchable.Select(p => SafeSearch(p, query.Trim(), limit, ct));
        var results = await Task.WhenAll(tasks);

        return results.SelectMany(r => r)
                      .OrderByDescending(r => r.Stars ?? 0)
                      .ThenByDescending(r => r.Pulls ?? 0)
                      .Take(limit)
                      .ToList();
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string registryId, string repository, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.IsEnabled && p.Id == registryId);
        return provider == null ? [] : await provider.GetTagsAsync(repository, 50, ct);
    }

    private async Task<IReadOnlyList<ImageSearchResult>> SafeSearch(IImageSearchProvider provider, string query, int limit, CancellationToken ct)
    {
        try
        {
            return await provider.SearchAsync(query, limit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Provider {Provider} threw during search", provider.Id);
            return [];
        }
    }
}
