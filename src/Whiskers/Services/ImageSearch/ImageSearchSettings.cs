namespace Whiskers.Services.ImageSearch;

/// <summary>
/// Configuration for the multi-registry image search (bound from the "ImageSearch" config section /
/// ImageSearch__* env vars). Docker Hub and GHCR are on by default; Harbor is opt-in via a URL.
/// </summary>
public class ImageSearchSettings
{
    public const string SectionName = "ImageSearch";

    public bool DockerHubEnabled { get; set; } = true;
    public bool GhcrEnabled { get; set; } = true;

    public HarborOptions Harbor { get; set; } = new();

    /// <summary>A self-hosted Harbor instance. Only enabled when <see cref="BaseUrl"/> is set.</summary>
    public class HarborOptions
    {
        /// <summary>Base URL of the Harbor instance, e.g. "https://harbor.example.com". Empty = disabled.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>Optional credentials for searching private projects (anonymous search otherwise).</summary>
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
