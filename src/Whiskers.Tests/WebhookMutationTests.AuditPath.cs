using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// INV-45 (S-04, S-05): every webhook delivery is exactly one complete event in the real <c>AuditLog</c> — the
/// delivery's own, under its run id — and each decision nested in it (the containers of a compose plan, the Git app
/// owner's deployment) is one event of its own operation, linked to the delivery. The deliveries that end before their
/// decision (no turn, changed, removed, given up) are driven in <see cref="WebhookReplayTests"/>.
/// </summary>
public sealed partial class WebhookMutationTests
{
    /// <summary>One delivery through the signed test (the same queue, worker and decision as an external call): its
    /// rows read by the delivery's operation (its run id, and everything nested in it), exactly one per operation, and
    /// the delivery's own event. Nothing else was written by the attempt.</summary>
    /// <param name="nestedDenialsDecided">Whether a refusal nested in the delivery came from the policy step (a
    /// protected resource, the kill switch: it names its decision) or before it (an owner below the level: none).</param>
    private async Task<(List<AuditLogEntity> Rows, AuditLogEntity Delivery)> DeliverAsync(WebhookEntity webhook,
        bool nestedDenialsDecided = true)
    {
        _trail = true;
        var scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        var written = await AuditTrail.AttemptAsync(scopes, () => Service().TriggerSignedTestAsync(webhook.WebhookId));
        string runId;
        using (var scope = _sp.CreateScope())
            runId = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.AsNoTracking()
                .Where(d => d.WebhookId == webhook.WebhookId).Select(d => d.RunId).SingleAsync();
        var rows = await AuditTrail.ForOperationAsync(scopes, runId);
        Assert.Equal(written.Select(r => r.Id).Order(), rows.Select(r => r.Id).Order());
        Assert.Equal(rows.Count, rows.Select(r => r.OperationId).Distinct(StringComparer.Ordinal).Count());
        var delivery = Assert.Single(rows, r => r.OperationId == runId);
        Assert.Null(delivery.ParentOperationId);
        Assert.EndsWith("webhook:" + webhook.WebhookId, delivery.PrincipalId);   // or "unowned:webhook:…"
        foreach (var nested in rows.Where(r => !ReferenceEquals(r, delivery)))
        {
            Assert.Contains(rows, r => r.OperationId == nested.ParentOperationId);
            AuditTrail.AssertComplete(nested, nested.Outcome!, nested.Action,
                PrincipalIds.KindOf(nested.PrincipalId) is MutationPrincipalKind.Webhook ? "Webhook" : "Automation",
                decided: nested.Outcome != AuditTrail.Denied || nestedDenialsDecided);
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
        AuditTrail.AssertComplete(executed, AuditTrail.Executed, "container.restart", "Webhook");
        Assert.Equal(AdminId, executed.OnBehalfOfId);

        _docker.AllowMutations = false;
        var (_, failed) = await DeliverAsync(Seed("restart", "web-1"));
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "container.restart", "Webhook");
        _docker.AllowMutations = true;

        _readOnly = true;
        var stopped = Seed("restart", "web-1");
        var (killedRows, _) = await DeliverAsync(stopped);
        AuditTrail.AssertComplete(Assert.Single(killedRows), AuditTrail.Denied, "container.restart", "Webhook");
        Assert.True(Stored(stopped.WebhookId).Enabled);   // a policy refusal leaves it on
        _readOnly = false;

        var demoted = Seed("restart", "web-1");
        _roles.Map[Admin] = AppRole.Viewer;
        var (_, refused) = await DeliverAsync(demoted);
        AuditTrail.AssertComplete(refused, AuditTrail.Denied, "container.restart", "Webhook", decided: false);
        Assert.False(Stored(demoted.WebhookId).Enabled);
        _roles.Map[Admin] = AppRole.Admin;

        var (unownedRows, unowned) = await DeliverAsync(Seed("restart", "web-1", owner: null));
        AuditTrail.AssertComplete(Assert.Single(unownedRows), AuditTrail.Denied, "container.restart", "Webhook", owned: false, decided: false);
        Assert.StartsWith("unowned:", unowned.PrincipalId);
    }

    /// <summary>S-04 (review B-1): the run's deadline passes while the gateway resolves the delivery's target with the
    /// production resolver (the host does not answer). The delivery had asked for its capability, so it is one Denied
    /// event under its run id — "cancelled before its decision", written although the run's token is cancelled — and
    /// nothing runs; the delivery ends failed.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_delivery_whose_deadline_passes_while_the_gateway_resolves_its_target_is_one_event()
    {
        _trail = true;
        _runTimeout = TimeSpan.FromSeconds(1);
        _resolverDocker = new FakeDocker(Container("web-1", "aaa111", "running")) { ListContainersHangs = true };
        var webhook = Seed("restart", "web-1");

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.True(_resolverDocker.ListingStarted.Task.IsCompleted, "the gateway never asked the host");
        var scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        WebhookDeliveryEntity delivery;
        var until = DateTime.UtcNow.AddSeconds(10);
        do
        {
            using var scope = _sp.CreateScope();
            delivery = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.AsNoTracking()
                .SingleAsync(d => d.WebhookId == webhook.WebhookId);
            if (delivery.State is not (WebhookDeliveryStore.Queued or WebhookDeliveryStore.Running)) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
        Assert.Equal(WebhookDeliveryStore.Failed, delivery.State);
        var row = AuditTrail.One(await AuditTrail.ForOperationAsync(scopes, delivery.RunId), AuditTrail.Denied,
            "container.restart", "Webhook", decided: false);
        Assert.Contains(MutationGateway.CancelledBeforeDecision, row.Details);
        Assert.Equal(AdminId, row.OnBehalfOfId);
        Assert.Empty(_docker.Actions);
    }

    /// <summary>S-04: a rebuild delivery is one <c>container.update</c> event — executed, failed.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Executed, AuditTrail.Failed)]
    public async Task Each_rebuild_delivery_is_one_complete_event_in_the_audit_log()
    {
        var (rows, executed) = await DeliverAsync(Seed("rebuild", "web-1"));
        Assert.Single(rows);
        AuditTrail.AssertComplete(executed, AuditTrail.Executed, "container.update", "Webhook");
        Assert.True(Ran("rebuild"));

        _docker.AllowMutations = false;
        var (failedRows, failed) = await DeliverAsync(Seed("rebuild", "web-1"));
        Assert.Single(failedRows);
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "container.update", "Webhook");
    }

    /// <summary>S-04: a compose deployment is the delivery's <c>compose.deploy</c> event and one event per planned
    /// container nested in it, each decided on its own; failed when compose up fails; refused by the kill switch
    /// (one event, nothing nested).</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Executed, AuditTrail.Denied, AuditTrail.Failed)]
    public async Task Each_compose_deploy_delivery_is_one_event_and_one_per_planned_container()
    {
        // Two services whose containers exist: `up` recreates both, so both are decided.
        _host.Files["/opt/duo/docker-compose.yml"] =
            "name: shop\nservices:\n  web:\n    image: nginx\n  db:\n    image: postgres\n";
        var webhook = Seed("deploy", "/opt/duo");
        var (plan, planError) = await WebhookTargets.ComposePlanAsync(new ComposePlanner(_docker, _host), webhook);
        Assert.True(plan is not null, planError);
        Assert.True(plan.Containers.Count >= 2, $"{plan.Containers.Count} planned containers");
        var (rows, delivery) = await DeliverAsync(webhook);
        AuditTrail.AssertComplete(delivery, AuditTrail.Executed, "compose.deploy", "Webhook");
        // Each container's decision is nested in the one before it (the deployment runs inside the last): every one is
        // a descendant of the delivery (DeliverAsync checks the links).
        var containers = rows.Where(r => !ReferenceEquals(r, delivery)).ToList();
        Assert.Equal(plan.Containers.Count, containers.Count);              // one event per planned container
        // compose.deploy acts on the host; each event names its own container in its arguments.
        foreach (var planned in plan.Containers)
            Assert.Single(containers, c => c.Arguments!.Contains(planned.Id, StringComparison.Ordinal));
        Assert.All(containers, c => Assert.Equal("compose.deploy", c.Action));
        Assert.True(Ran("deploy"));

        _host.Effect = command => command.Contains(" up ", StringComparison.Ordinal)
            ? new Whiskers.Services.Server.CommandResult { ExitCode = 1, Output = "compose up failed" }
            : null;
        var (_, failed) = await DeliverAsync(Seed("deploy", "/opt/duo"));
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "compose.deploy", "Webhook");
        _host.Effect = null;

        _readOnly = true;
        var (killedRows, killed) = await DeliverAsync(Seed("deploy", "/opt/duo"));
        Assert.Single(killedRows);
        AuditTrail.AssertComplete(killed, AuditTrail.Denied, "compose.deploy", "Webhook");
    }

    /// <summary>S-04: a stored action none of the four is refused before any decision — one Denied event under the
    /// delivery's run id, as its raw name, which names no capability; nothing runs.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_delivery_of_an_unknown_action_is_one_denied_event()
    {
        var (rows, delivery) = await DeliverAsync(Seed("reboot", "web-1"));

        Assert.Single(rows);
        AuditTrail.AssertComplete(delivery, AuditTrail.Denied, "reboot", "Webhook", decided: false, targetId: "");
        Assert.Equal("unknown", delivery.TargetType);
        // Keyed as the worker's own refusal, not as a delivery that did not run (review rounds 6 and 7): a reconciliation
        // settles it as refused and does not log it again.
        Assert.Equal(Whiskers.Services.Mutations.AuditLogMutationSink.KeyedEventId(
            Whiskers.Services.Webhooks.WebhookService.RefusedEventKey(delivery.OperationId!)), delivery.EventId);
        Assert.Empty(_docker.Actions);
    }

    /// <summary>S-05 via S-04: a Git deployment fired by a push — the delivery's event, the app owner's deployment
    /// nested in it with every planned container decided, each one event; failed when compose up fails; refused by the
    /// kill switch; refused for the app's owner who lost the role while the webhook's owner holds it.</summary>
    [Fact]
    [AuditPath("S-05", AuditTrail.Executed, AuditTrail.Denied, AuditTrail.Failed)]
    public async Task Each_git_deployment_by_webhook_is_one_complete_event_per_decision()
    {
        var (rows, delivery) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(delivery, AuditTrail.Executed, "gitdeploy.deploy", "Webhook");
        var deployment = Assert.Single(rows, r => r.ParentOperationId == delivery.OperationId && r.Action == "gitdeploy.deploy"
                                                  && r.PrincipalId == "gitdeploy:" + AppId);
        AuditTrail.AssertComplete(deployment, AuditTrail.Executed, "gitdeploy.deploy", "Automation");
        Assert.True(Ran("git-deploy"));

        _host.Effect = command => command.Contains(" up ", StringComparison.Ordinal)
            ? new Whiskers.Services.Server.CommandResult { ExitCode = 1, Output = "compose up failed" }
            : null;
        var (_, failed) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(failed, AuditTrail.Failed, "gitdeploy.deploy", "Webhook");
        _host.Effect = null;

        _readOnly = true;
        var (killedRows, _) = await DeliverAsync(Seed("git-deploy", AppId));
        AuditTrail.AssertComplete(Assert.Single(killedRows), AuditTrail.Denied, "gitdeploy.deploy", "Webhook");
        _readOnly = false;

        // The app's owner lost Admin, the webhook's owner holds it: the delivery is decided and refused for the app
        // owner's refusal, and the app owner's deployment is one Denied event of its own, nested in it.
        const string Other = "other@example.com";
        _whitelist.Allowed.Add(Other);
        _roles.Map[Other] = AppRole.Admin;
        _roles.Map[Admin] = AppRole.Viewer;
        var ups = _host.Executed.Count(c => c.Contains("docker compose") && c.Contains(" up "));
        var (appRows, appRefused) = await DeliverAsync(Seed("git-deploy", AppId, owner: PrincipalIds.User(Other)),
            nestedDenialsDecided: false);
        Assert.Equal(PrincipalIds.User(Other), appRefused.OnBehalfOfId);
        Assert.NotEqual(AuditTrail.Executed, appRefused.Outcome);
        AuditTrail.AssertComplete(appRefused, appRefused.Outcome!, "gitdeploy.deploy", "Webhook");
        var appDeployment = Assert.Single(appRows, r => r.ParentOperationId == appRefused.OperationId);
        AuditTrail.AssertComplete(appDeployment, AuditTrail.Denied, "gitdeploy.deploy", "Automation", decided: false);
        Assert.Equal(AdminId, appDeployment.OnBehalfOfId);
        Assert.Equal(ups, _host.Executed.Count(c => c.Contains("docker compose") && c.Contains(" up ")));
    }
}
