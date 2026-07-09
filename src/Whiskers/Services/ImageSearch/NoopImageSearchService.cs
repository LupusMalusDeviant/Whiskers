namespace Whiskers.Services.ImageSearch;

/// <summary>The Core's default <see cref="IImageSearchService"/> for when the Deployment module is off. Only
/// the AppStore page consumes it (gated by <c>ModuleGuard</c>), so this no-op simply makes that page's
/// injection safe without a separate <c>*View</c> split — it returns no registries and no results. The real
/// <see cref="ImageSearchService"/> wins by last-registration when the module is enabled. Soft-dependency-via-
/// no-op-Core-contract pattern (RoadToSAP §2.1).</summary>
public sealed class NoopImageSearchService : IImageSearchService
{
    public IReadOnlyList<ImageRegistryInfo> GetRegistries() => Array.Empty<ImageRegistryInfo>();

    public Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, string? registryId, int limit = 25, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ImageSearchResult>>(Array.Empty<ImageSearchResult>());

    public Task<IReadOnlyList<string>> GetTagsAsync(string registryId, string repository, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
