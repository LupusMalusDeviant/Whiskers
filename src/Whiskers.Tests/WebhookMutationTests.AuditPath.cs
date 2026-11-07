using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// INV-45 (S-04, S-05): every webhook delivery is exactly one complete event in the real <c>AuditLog</c> — the
/// delivery's own, under its run id — and each decision nested in it (the containers of a compose plan, the Git app
/// owner's deployment) is one event of its own operation, linked to the delivery.
/// </summary>
public sealed partial class WebhookMutationTests
{
    /// <summary>One delivery through the signed test (the same queue, worker and decision as an external call): its
    /// rows, exactly one per operation, and the delivery's own event.</summary>
    private async Task<(List<AuditLogEntity> Rows, AuditLogEntity Delivery)> DeliverAsync(WebhookEntity webhook)
    {
        _trail = true;
        var scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        var rows = await AuditTrail.AttemptAsync(scopes, () => Service().TriggerSignedTestAsync(webhook.WebhookId));
        Assert.Equal(rows.Count, rows.Select(r => r.OperationId).Distinct(StringComparer.Ordinal).Count());
        var delivery = Assert.Single(rows, r => r.ParentOperationId is null);
        Assert.EndsWith("webhook:" + webhook.WebhookId, delivery.PrincipalId);   // or "unowned:webhook:…"
        foreach (var nested in rows.Where(r => !ReferenceEquals(r, delivery)))
        {
            Assert.Contains(rows, r => r.OperationId == nested.ParentOperationId);
            AuditTrail.AssertComplete(nested, nested.Outcome!, nested.Action, "Automation");
        }
        return (rows, delivery);
    }

    /// <summary>S-04: a restart delivery — executed, failed (the daemon refused), refused by the kill switch, refused
    /// for an owner who lost the role, refused for a webhook without an owner.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Executed, AuditTrail.Denied, AuditTrail.Failed)]
    public async Task Each_webhook_delivery_is_one_complete_event_in_the_audit_log()
    {
        var (_, executed) = await DeliverAsync(Seed("restart", "web-1"));
        AuditTrail.AssertComplete(executed, AuditTrail.Executed, "container.restart", "Automation");
        Assert.Equal(AdminId, executed.OnBehalfOfId);

        _docker.AllowMutations = false;
        var (_, failed) = await DeliverAsync(Seed("restart", "web-1"));
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "container.restart", "Automation");
        _docker.AllowMutations = true;

        _readOnly = true;
        var stopped = Seed("restart", "web-1");
        var (killedRows, _) = await DeliverAsync(stopped);
        AuditTrail.AssertComplete(Assert.Single(killedRows), AuditTrail.Denied, "container.restart", "Automation");
        Assert.True(Stored(stopped.WebhookId).Enabled);   // a policy refusal leaves it on
        _readOnly = false;

        var demoted = Seed("restart", "web-1");
        _roles.Map[Admin] = AppRole.Viewer;
        var (_, refused) = await DeliverAsync(demoted);
        AuditTrail.AssertComplete(refused, AuditTrail.Denied, "container.restart", "Automation");
        Assert.False(Stored(demoted.WebhookId).Enabled);
        _roles.Map[Admin] = AppRole.Admin;

        var (unownedRows, unowned) = await DeliverAsync(Seed("restart", "web-1", owner: null));
        AuditTrail.AssertComplete(Assert.Single(unownedRows), AuditTrail.Denied, "container.restart", "Automation", owned: false);
        Assert.StartsWith("unowned:", unowned.PrincipalId);
    }

    /// <summary>S-05 via S-04: a Git deployment fired by a push — the delivery's event, the app owner's deployment
    /// nested in it with every planned container decided, each one event; failed when compose up fails; refused by the
    /// kill switch.</summary>
    [Fact]
    [AuditPath("S-05", AuditTrail.Executed, AuditTrail.Denied, AuditTrail.Failed)]
    public async Task Each_git_deployment_by_webhook_is_one_complete_event_per_decision()
    {
        var (rows, delivery) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(delivery, AuditTrail.Executed, "gitdeploy.deploy", "Automation");
        var deployment = Assert.Single(rows, r => r.ParentOperationId == delivery.OperationId && r.Action == "gitdeploy.deploy"
                                                  && r.PrincipalId == "gitdeploy:" + AppId);
        AuditTrail.AssertComplete(deployment, AuditTrail.Executed, "gitdeploy.deploy", "Automation");
        Assert.True(Ran("git-deploy"));

        _host.Effect = command => command.Contains(" up ", StringComparison.Ordinal)
            ? new Whiskers.Services.Server.CommandResult { ExitCode = 1, Output = "compose up failed" }
            : null;
        var (_, failed) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "gitdeploy.deploy", "Automation");
        _host.Effect = null;

        _readOnly = true;
        var (killedRows, _) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(Assert.Single(killedRows), AuditTrail.Denied, "gitdeploy.deploy", "Automation");
    }
}
