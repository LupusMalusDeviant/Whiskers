namespace Whiskers.Models;

/// <summary>A container registry the user manages in the UI (missingFeatures F8): used to
/// authenticate image PULLS for private registries. The credential (password/token) lives in the
/// VAULT (<c>registry-cred:{Id}</c>), never here.</summary>
public class RegistryConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>Registry host as it appears in image references, e.g. <c>ghcr.io</c>,
    /// <c>harbor.example.com:5000</c>, or <c>docker.io</c> for Docker Hub.</summary>
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Whether a vault credential exists (the secret itself is vault-only).</summary>
    public bool HasCredential { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RegistryConfigData
{
    public List<RegistryConfig> Registries { get; set; } = new();
}
