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
    [Whiskers.Services.Mutations.InventoryLookup]
    Task<GitDeployApp?> GetAppAsync(string appId);

    /// <summary>Creates/updates the app. A non-null <paramref name="token"/> is stored in the vault;
    /// null keeps an existing token; an empty string removes it. Whoever saves becomes the owner
    /// (<paramref name="ownerId"/>, a principal id, R1-08). An update must carry the <see cref="GitDeployApp.Revision"/>
    /// it was loaded with; a stale one throws <see cref="GitDeployConflictException"/>. Contract 0020: a held app
    /// (<see cref="GitDeployApp.HeldReason"/>) stays held, whatever the caller's copy says, unless
    /// <paramref name="releaseHold"/> — only the Git deploy page's save passes it, as the contract accepts for that page;
    /// an MCP save or token change never releases a hold.</summary>
    [MutatingOperation("gitdeploy.save")]
    Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token, string ownerId, bool releaseHold = false);

    /// <summary>Replaces the stored app's repository token (empty string removes it) and keeps every other setting
    /// (contract 0001). The app must still be at <paramref name="revision"/>, otherwise
    /// <see cref="GitDeployConflictException"/>; a missing app throws <see cref="KeyNotFoundException"/>. Like a save,
    /// whoever sets it becomes the owner (<paramref name="ownerId"/>, R1-08): the inputs a deployment runs with changed.
    /// A held app stays held (contract 0020).</summary>
    [MutatingOperation("gitdeploy.set_token")]
    Task<GitDeployApp> SetTokenAsync([MutationTarget] string appId, int revision, string token, string ownerId);

    /// <summary>Takes the app over (R1-08): sets <paramref name="ownerId"/> only while the stored owner is still
    /// <paramref name="expectedOwnerId"/> (empty = no owner). False when someone else changed it first.</summary>
    [MutatingOperation("gitdeploy.save")]
    Task<bool> SetOwnerAsync(string appId, string ownerId, string? expectedOwnerId);

    /// <summary>Deletes the app, its vault token and its workspace on the target server.</summary>
    [MutatingOperation("gitdeploy.delete")]
    Task DeleteAppAsync([MutationTarget] string appId);

    /// <summary>Runs a full deploy (fetch → plan → build → up) of <paramref name="app"/> — the snapshot the
    /// deployment was decided on (R1-08) — streaming human-readable progress lines. Returns success + the deployed
    /// commit sha (or the failing step's error). The stored app must still be at the snapshot's
    /// <see cref="GitDeployApp.Revision"/> before the fetch and again before the build; otherwise it is refused. After
    /// fetching, the deployment is planned (<see cref="ComposePlan"/>, R1-07) and <paramref name="gate"/> decides every
    /// container of the plan before build and up run exactly as planned.</summary>
    [MutatingOperation("gitdeploy.deploy")]
    Task<(bool Success, string Output)> DeployAsync(GitDeployApp app, ComposePlanGate gate,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Contract 0020: <c>Owner := to</c> only while it is <paramref name="fromOwnerId"/>; <c>Revision + 1</c>;
    /// <see cref="GitDeployApp.HeldReason"/> := the handover text (D6). Called only from <c>AutomationHandover</c>.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverWrite> HandOverAsync(string appId, string fromOwnerId, string toOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hand an app over.");

    /// <summary>Contract 0020 (D11 c): held (<see cref="GitDeployApp.HeldReason"/>, <c>Revision + 1</c>) only while the
    /// owner is <paramref name="ownerId"/>; the owner stays. Inside <c>account.create</c> only.</summary>
    [MutatingOperation("automation.hold")]
    Task<HandoverWrite> HoldAsync(string appId, string ownerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hold an app.");

    /// <summary>Contract 0020 (D7): clears <see cref="GitDeployApp.HeldReason"/> only while the owner is
    /// <paramref name="expectedOwnerId"/>; the owner is kept (a resume never re-owns). False when the app is gone, has
    /// another owner, or is not held.</summary>
    [MutatingOperation("gitdeploy.save")]
    Task<bool> ResumeAsync(string appId, string expectedOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot resume an app.");
}

/// <summary>The app was changed since it was loaded (R1-08): the save or deployment is refused rather than run with
/// other inputs. <see cref="NameTaken"/>: refused because another app has the name already (contract 0001).</summary>
public sealed class GitDeployConflictException(string message, bool nameTaken = false) : Exception(message)
{
    public bool NameTaken { get; } = nameTaken;
}
