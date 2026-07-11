using Whiskers.Models;

namespace Whiskers.Services.GitDeploy;

/// <summary>Git-based deployments (F5): clone/update a repo ON THE TARGET SERVER and bring it up
/// with docker compose. Configs live in a JSON store; a private repo's access token lives in the
/// vault (<c>git-token:{appId}</c>) and is only ever materialized as a 0600 askpass file on the
/// target. Webhooks trigger redeploys via the "git-deploy" action (soft dependency: the Webhooks
/// module calls this through the Core contract; a no-op default answers when the module is off).</summary>
public interface IGitDeployService
{
    Task<List<GitDeployApp>> GetAppsAsync();
    Task<GitDeployApp?> GetAppAsync(string appId);

    /// <summary>Creates/updates the app. A non-null <paramref name="token"/> is stored in the vault;
    /// null keeps an existing token; an empty string removes it.</summary>
    Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token);

    /// <summary>Deletes the app, its vault token and its workspace on the target server.</summary>
    Task DeleteAppAsync(string appId);

    /// <summary>Runs a full deploy (fetch → build → up), streaming human-readable progress lines.
    /// Returns success + the deployed commit sha (or the failing step's error).</summary>
    Task<(bool Success, string Output)> DeployAsync(string appId, IProgress<string>? progress = null, CancellationToken ct = default);
}
