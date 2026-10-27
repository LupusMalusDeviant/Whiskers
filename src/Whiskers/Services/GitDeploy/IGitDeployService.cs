using Whiskers.Services.Mutations;
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
    /// null keeps an existing token; an empty string removes it. Whoever saves becomes the owner
    /// (<paramref name="ownerId"/>, a principal id, R1-08). An update must carry the <see cref="GitDeployApp.Revision"/>
    /// it was loaded with; a stale one throws <see cref="GitDeployConflictException"/>.</summary>
    [MutatingOperation("gitdeploy.save")]
    Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token, string ownerId);

    /// <summary>Takes the app over (R1-08): sets <paramref name="ownerId"/> only while the stored owner is still
    /// <paramref name="expectedOwnerId"/> (empty = no owner). False when someone else changed it first.</summary>
    [MutatingOperation("gitdeploy.save")]
    Task<bool> SetOwnerAsync(string appId, string ownerId, string? expectedOwnerId);

    /// <summary>Deletes the app, its vault token and its workspace on the target server.</summary>
    [MutatingOperation("gitdeploy.delete")]
    Task DeleteAppAsync(string appId);

    /// <summary>Runs a full deploy (fetch → plan → build → up) of <paramref name="app"/> — the snapshot the
    /// deployment was decided on (R1-08) — streaming human-readable progress lines. Returns success + the deployed
    /// commit sha (or the failing step's error). The stored app must still be at the snapshot's
    /// <see cref="GitDeployApp.Revision"/> before the fetch and again before the build; otherwise it is refused. After
    /// fetching, the deployment is planned (<see cref="ComposePlan"/>, R1-07) and <paramref name="gate"/> decides every
    /// container of the plan before build and up run exactly as planned.</summary>
    [MutatingOperation("gitdeploy.deploy")]
    Task<(bool Success, string Output)> DeployAsync(GitDeployApp app, ComposePlanGate gate,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>The app was changed since it was loaded (R1-08): the save or deployment is refused rather than run with
/// other inputs.</summary>
public sealed class GitDeployConflictException(string message) : Exception(message);
