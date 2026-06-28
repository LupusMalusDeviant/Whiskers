namespace ServerWatch.Services.ImageSearch;

/// <summary>A single image match returned from a registry ("marketplace") search.</summary>
public class ImageSearchResult
{
    /// <summary>Provider id the result came from, e.g. "dockerhub", "ghcr", "harbor". Routes the deploy back to the right marketplace.</summary>
    public string RegistryId { get; init; } = "";

    /// <summary>Human-readable provider name for the source badge, e.g. "Docker Hub".</summary>
    public string RegistryName { get; init; } = "";

    /// <summary>Repository name as shown to the user, e.g. "nginx" or "owner/app".</summary>
    public string Name { get; init; } = "";

    /// <summary>Pullable image reference WITHOUT a tag, e.g. "nginx", "ghcr.io/owner/app", "harbor.example.com/lib/app".</summary>
    public string PullReference { get; init; } = "";

    public string? Description { get; init; }
    public long? Stars { get; init; }
    public long? Pulls { get; init; }
    public bool IsOfficial { get; init; }
    public bool IsVerified { get; init; }
}

/// <summary>Describes an enabled registry ("marketplace") for the UI selector.</summary>
/// <param name="Id">Stable provider id (matches <see cref="ImageSearchResult.RegistryId"/>).</param>
/// <param name="Name">Display name.</param>
/// <param name="SupportsSearch">True if the provider supports free-text discovery; false = exact-reference lookup only.</param>
public record ImageRegistryInfo(string Id, string Name, bool SupportsSearch);
