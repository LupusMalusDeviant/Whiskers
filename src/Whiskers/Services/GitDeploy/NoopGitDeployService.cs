using Whiskers.Models;

namespace Whiskers.Services.GitDeploy;

/// <summary>Core default when the GitDeploy module is off (soft-dependency-via-no-op pattern,
/// RoadToSAP §2.1): the webhook "git-deploy" action resolves this and fails gracefully instead of
/// 500ing; reads return empty; mutations throw.</summary>
public sealed class NoopGitDeployService : IGitDeployService
{
    private const string Disabled =
        "The GitDeploy module is disabled (set Features:gitdeploy:Enabled=true to enable git deployments).";

    public Task<List<GitDeployApp>> GetAppsAsync() => Task.FromResult(new List<GitDeployApp>());
    public Task<GitDeployApp?> GetAppAsync(string appId) => Task.FromResult<GitDeployApp?>(null);
    public Task<GitDeployApp> SaveAppAsync(GitDeployApp app, string? token, string ownerId) => throw new InvalidOperationException(Disabled);
    public Task<bool> SetOwnerAsync(string appId, string ownerId, string? expectedOwnerId) => throw new InvalidOperationException(Disabled);
    public Task DeleteAppAsync(string appId) => Task.CompletedTask;
    public Task<(bool Success, string Output)> DeployAsync(GitDeployApp app, Whiskers.Services.Mutations.ComposePlanGate gate,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult((false, "Git deployments are disabled."));
}
