namespace Whiskers.Services.ImageSearch;

/// <summary>
/// One container registry / "marketplace" that can be searched for images.
/// Implementations are registered as <see cref="IImageSearchProvider"/> and aggregated by
/// <see cref="IImageSearchService"/>. A provider that returns false for <see cref="IsEnabled"/>
/// is omitted from the UI (e.g. Harbor without a configured URL).
/// </summary>
public interface IImageSearchProvider
{
    /// <summary>Stable id, e.g. "dockerhub". Used to route a deploy back to the originating marketplace.</summary>
    string Id { get; }

    /// <summary>Display name for the source badge, e.g. "Docker Hub".</summary>
    string DisplayName { get; }

    /// <summary>False when the provider is not configured/usable — hidden from the UI and skipped by the aggregator.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether the provider supports free-text discovery. When false the query is treated as an
    /// exact repository reference (e.g. GHCR has no anonymous full-text search).
    /// </summary>
    bool SupportsSearch { get; }

    /// <summary>Search the registry. Returns an empty list on any failure (never throws for normal "not found"/auth cases).</summary>
    Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string query, int limit, CancellationToken ct);

    /// <summary>List available tags for a repository, most-recent first where the API allows. Empty on failure.</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(string repository, int limit, CancellationToken ct);
}
