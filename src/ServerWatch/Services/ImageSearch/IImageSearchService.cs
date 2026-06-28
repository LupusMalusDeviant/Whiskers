namespace ServerWatch.Services.ImageSearch;

/// <summary>
/// Aggregates the configured <see cref="IImageSearchProvider"/>s so the UI can search across
/// multiple registries ("marketplaces") and distinguish where each result came from.
/// </summary>
public interface IImageSearchService
{
    /// <summary>Enabled registries for the marketplace selector.</summary>
    IReadOnlyList<ImageRegistryInfo> GetRegistries();

    /// <summary>
    /// Search a single registry (<paramref name="registryId"/>) or, when null, all search-capable
    /// registries in parallel. Results are merged and ordered by popularity (stars).
    /// </summary>
    Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, string? registryId, int limit = 25, CancellationToken ct = default);

    /// <summary>List tags for a repository within a specific registry.</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(string registryId, string repository, CancellationToken ct = default);
}
