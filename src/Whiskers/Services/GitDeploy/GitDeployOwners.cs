using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.GitDeploy;

/// <summary>Who a Git deployment without a person at the keyboard (a webhook) runs as, and how (R1-08).</summary>
public static class GitDeployOwners
{
    /// <summary>The app as an automation actor: decided on its owner's current authority. The owner is only ever
    /// set by a person on the Git deploy page (save or take over), so that person is also its provenance: the PC-D1
    /// rules apply, not an agent's (R1-09).</summary>
    public static AutomationActor ActorOf(GitDeployApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var owner = string.IsNullOrWhiteSpace(app.Owner) ? null : app.Owner;
        return new AutomationActor("gitdeploy:" + app.Id, $"Git deployment '{app.Name}'", owner, owner);
    }

    /// <summary>
    /// Deploys the stored app on its owner's authority, re-checked now: the deployment and every container of its
    /// plan are decided as the owner through the automation runner. An app without an owner (saved before owners
    /// were recorded) is refused until an admin takes it over, as is one whose owner lost the authority — before
    /// anything is fetched. The snapshot the decision was made on is what runs.
    /// </summary>
    public static async Task<(bool Success, string Output)> DeployAsOwnerAsync(IGitDeployService gitDeploy,
        IAutomationMutationRunner? automation, string appId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gitDeploy);
        var app = await gitDeploy.GetAppAsync(appId);
        if (app is null) return (false, "Git deploy app not found.");
        if (string.IsNullOrWhiteSpace(app.Owner))
            return (false, $"'{app.Name}' has no owner: an admin must take it over on the Git deploy page first.");
        if (automation is null) return (false, "Automated deployments are not available.");

        var actor = ActorOf(app);
        var outcome = await automation.RunAsync(actor, "gitdeploy.deploy",
            new { appId = app.Id, name = app.Name, serverId = app.ServerId, branch = app.Branch, revision = app.Revision },
            token => gitDeploy.DeployAsync(app, automation.GateFor(actor, "gitdeploy.deploy"), ct: token), ct);
        if (outcome.Error is MutationRefusedException refused) return (false, refused.Message);
        return outcome.Status is MutationStatus.Executed or MutationStatus.Failed && outcome.Value.Output is not null
            ? outcome.Value
            : (false, outcome.Reason ?? $"The deployment was {outcome.Status}.");
    }
}
