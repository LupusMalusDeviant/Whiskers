namespace Whiskers.Models;

/// <summary>A git-based deployment (F5): a repo that is cloned/updated ON THE TARGET SERVER and
/// brought up with docker compose. Deliberately lean — no buildpacks, no PR previews (post-1.0).
/// An optional access token for private repos lives in the VAULT (<c>git-token:{Id}</c>), never here.</summary>
public class GitDeployApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>HTTPS clone URL (SSH remotes are out of scope for v1 — no key distribution).</summary>
    public string RepoUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>Compose file path RELATIVE to the repo root.</summary>
    public string ComposePath { get; set; } = "docker-compose.yml";
    public string ServerId { get; set; } = "local";
    /// <summary>Whether a vault token exists for this app (the token itself is vault-only).</summary>
    public bool HasToken { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDeployedAt { get; set; }
    public string? LastDeployedSha { get; set; }
    public bool? LastDeploySucceeded { get; set; }
}

public class GitDeployData
{
    public List<GitDeployApp> Apps { get; set; } = new();
}
