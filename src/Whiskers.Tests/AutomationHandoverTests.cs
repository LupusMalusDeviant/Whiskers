using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Scheduler;
using static Whiskers.Tests.AutomationHandoverWorld;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0020 (INV-66): an Admin hands the automation of an owner who can no longer act over to another person who
/// can act now. Every handed-over item is off until someone switches it on; a resume never re-owns; the first run after
/// it names the handover and the resumer. Real stores on a temp database and data directory, a fake resolver.
/// </summary>
public sealed class AutomationHandoverTests : IDisposable
{
    private readonly AutomationHandoverWorld _w = new();
    private const string AppA = "0123456789ab";

    public void Dispose() => _w.Dispose();

    private static AutomationItemRef TaskItem(string id) => new(AutomationKind.ScheduledTask, id);
    private static AutomationItemRef Hook(string id) => new(AutomationKind.Webhook, id);
    private static AutomationItemRef App(string id) => new(AutomationKind.GitDeployApp, id);
    private static AutomationItemRef Trigger(string id) => new(AutomationKind.AiTrigger, id);
    private static AutomationItemRef Check(string id) => new(AutomationKind.EndpointCheck, id);
    private static AutomationItemRef Feature(string id) => new(AutomationKind.FleetFeature, id);

    private async Task<HandoverResult> HandOverAsync(string from, string to, params AutomationItemRef[] items)
    {
        using var scope = Decision("automation.handover");
        return await _w.Handover().HandOverAsync(from, to, items);
    }

    private static Task<MutationOutcome<bool>> RunTaskAsync(AutomationMutationRunner runner, ScheduledTaskEntity task,
        Action<IReadOnlyList<MutationDomainResult>>? seen = null) =>
        runner.RunAsync(AutomationActor.ForTask(task.TaskId, task.Name, task.CreatedBy, task.CreatedVia), "container.restart",
            new { containerId = "web-1" }, (ResolvedTarget _, CancellationToken _) =>
            {
                seen?.Invoke(MutationScope.Current!.Results);
                return System.Threading.Tasks.Task.CompletedTask;
            });

    // ---- who is listed, what is eligible ----------------------------------------------------------------------------

    [Fact]
    public async Task Unusable_owners_lists_deleted_unverified_disallowed_key_and_demoted_owners_with_counts()
    {
        const string unverified = "unverified@example.com", removed = "removed@example.com", demoted = "demoted@example.com";
        _w.Accounts.Unverified.Add(unverified);
        _w.Whitelist.Allowed.Add(demoted);
        _w.Roles.Map[demoted] = AppRole.Viewer;   // can sign in, but lacks the level a restart needs
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await _w.SeedTaskAsync("t3", PrincipalIds.User(unverified));
        await _w.SeedTaskAsync("t4", PrincipalIds.User(removed));
        await _w.SeedWebhookAsync("h2", "key:k-gone");
        await _w.SeedTaskAsync("t5", PrincipalIds.User(demoted));
        await _w.SeedTaskAsync("t6", StillId);   // can still run

        var owners = await _w.Handover().UnusableOwnersAsync();

        var byId = owners.ToDictionary(o => o.OwnerId);
        Assert.Equal(5, byId.Count);
        Assert.Equal(OwnerState.AccountDeleted, byId[GoneId].Description.State);
        Assert.Equal(2, byId[GoneId].Counts[AutomationKind.ScheduledTask]);
        Assert.Equal(1, byId[GoneId].Counts[AutomationKind.Webhook]);
        Assert.Equal(OwnerState.Unverified, byId[PrincipalIds.User(unverified)].Description.State);
        Assert.Equal(OwnerState.NotAllowedToSignIn, byId[PrincipalIds.User(removed)].Description.State);
        Assert.Equal(OwnerState.KeyDeleted, byId["key:k-gone"].Description.State);
        Assert.Equal(OwnerState.Active, byId[PrincipalIds.User(demoted)].Description.State);
        Assert.DoesNotContain(StillId, byId.Keys);
    }

    [Fact]
    public async Task An_unverified_owner_is_eligible_although_LostAuthority_is_null()
    {
        const string unverified = "unverified@example.com";
        _w.Accounts.Unverified.Add(unverified);
        _w.Whitelist.Allowed.Add(unverified);
        _w.Roles.Map[unverified] = AppRole.Admin;
        await _w.SeedTaskAsync("t1", PrincipalIds.User(unverified));
        var task = await _w.TaskAsync("t1");
        Assert.Null(_w.Runner.LostAuthority(AutomationActor.ForTask("t1", task.Name, task.CreatedBy, task.CreatedVia),
            "container.restart"));

        var item = Assert.Single(await _w.Handover().OwnedByAsync(PrincipalIds.User(unverified)));

        Assert.Equal(HandoverEligibility.CannotRunOnOwner, item.Eligibility);
        Assert.True((await _w.Handover().CheckAsync(PrincipalIds.User(unverified), NewId, [TaskItem("t1")])).Ok);
    }

    [Fact]
    public async Task An_unknown_capability_or_a_resolver_exception_makes_the_item_CannotJudge_and_refuses_the_handover()
    {
        await _w.SeedWebhookAsync("h1", GoneId, action: "frobnicate");
        const string broken = "broken@example.com";
        await _w.SeedTaskAsync("t1", PrincipalIds.User(broken));
        _w.Resolver = new ThrowingResolver(_w.Resolver, PrincipalIds.User(broken));

        var hook = Assert.Single(await _w.Handover().OwnedByAsync(GoneId));
        var task = Assert.Single(await _w.Handover().OwnedByAsync(PrincipalIds.User(broken)));

        Assert.Equal(HandoverEligibility.CannotJudge, hook.Eligibility);
        Assert.Equal(HandoverEligibility.CannotJudge, task.Eligibility);
        await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(GoneId, NewId, Hook("h1")));
        await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(PrincipalIds.User(broken), NewId, TaskItem("t1")));
        Assert.Equal(GoneId, (await _w.WebhookAsync("h1")).Owner);
        Assert.Equal(PrincipalIds.User(broken), (await _w.TaskAsync("t1")).CreatedBy);
    }

    [Fact]
    public async Task A_runner_with_the_default_LostAuthority_is_CannotJudge()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        var handover = _w.Handover(new DefaultRunner());

        var item = Assert.Single(await handover.OwnedByAsync(GoneId));

        Assert.Equal(HandoverEligibility.CannotJudge, item.Eligibility);
        Assert.False((await handover.CheckAsync(GoneId, NewId, [TaskItem("t1")])).Ok);
    }

    [Fact]
    public async Task An_owner_whose_items_can_still_run_is_not_listed_and_its_items_are_refused()
    {
        await _w.SeedTaskAsync("t1", StillId);

        Assert.Empty(await _w.Handover().UnusableOwnersAsync());
        Assert.Equal(HandoverEligibility.RunsOnOwner, Assert.Single(await _w.Handover().OwnedByAsync(StillId)).Eligibility);
        var refused = await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(StillId, NewId, TaskItem("t1")));
        Assert.Contains("still runs on its owner", refused.Message);
        Assert.Equal(StillId, (await _w.TaskAsync("t1")).CreatedBy);
    }

    // ---- the handover --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_handed_over_item_is_off_and_runs_on_the_new_owner_only_after_resume()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await _w.SeedAppAsync(AppA, GoneId);
        await _w.SeedTriggerAsync("g1", GoneId);
        var check = await _w.SeedCheckAsync(GoneId);
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, GoneId);

        var result = await HandOverAsync(GoneId, NewId, TaskItem("t1"), Hook("h1"), App(AppA), Trigger("g1"), Check(check.Id),
            Feature(IAutomationOwnerStore.AutoPatch));

        Assert.All(result.Items, i => Assert.Equal(HandoverItemStatus.HandedOver, i.Status));
        var task = await _w.TaskAsync("t1");
        Assert.Equal((NewId, false), (task.CreatedBy, task.Enabled));
        var hook = await _w.WebhookAsync("h1");
        Assert.Equal((NewId, false), (hook.Owner, hook.Enabled));
        var app = (await _w.Git.GetAppAsync(AppA))!;
        Assert.Equal(NewId, app.Owner);
        Assert.Equal(Whiskers.Services.GitDeploy.GitDeployService.HandoverHeldReason, app.HeldReason);
        var trigger = Assert.Single(_w.Triggers.Triggers);
        Assert.Equal((NewId, false), (trigger.OwnerId, trigger.Enabled));
        var stored = _w.Checks.List().Single();
        Assert.Equal((NewId, false), (stored.OwnerId, stored.Enabled));
        Assert.Null(_w.Fleet.Get(IAutomationOwnerStore.AutoPatch));
        Assert.Equal(NewId, _w.Fleet.GetBlocked(IAutomationOwnerStore.AutoPatch)?.OwnerId);

        // The task does not run while it is off (the scheduler only picks enabled tasks); switched on, it runs as the new owner.
        using (Decision("scheduler.update"))
        {
            task.Enabled = true;
            await _w.Scheduler.UpdateTaskAsync(task);
        }
        var run = await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"));
        Assert.Equal(MutationStatus.Executed, run.Status);
        Assert.Equal(NewId, _w.Audit.Principals.Last().OnBehalfOf?.Id);
    }

    [Fact]
    public async Task An_item_that_was_on_is_switched_off_and_reported_as_was_on()
    {
        await _w.SeedTaskAsync("on", GoneId, enabled: true);
        await _w.SeedTaskAsync("off", GoneId, enabled: false);

        var result = await HandOverAsync(GoneId, NewId, TaskItem("on"), TaskItem("off"));

        Assert.True(result.Items.Single(i => i.Ref.Id == "on").WasOn);
        Assert.False(result.Items.Single(i => i.Ref.Id == "off").WasOn);
        Assert.False((await _w.TaskAsync("on")).Enabled);
    }

    [Fact]
    public async Task A_new_owner_without_the_level_for_one_item_refuses_the_whole_handover_and_writes_nothing()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedAppAsync(AppA, GoneId);
        _w.Roles.Map[NewOwner] = AppRole.Viewer;

        var check = await _w.Handover().CheckAsync(GoneId, NewId, [TaskItem("t1"), App(AppA)]);
        Assert.False(check.Ok);
        await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(GoneId, NewId, TaskItem("t1"), App(AppA)));

        Assert.Equal((GoneId, true), ((await _w.TaskAsync("t1")).CreatedBy, (await _w.TaskAsync("t1")).Enabled));
        Assert.Equal(GoneId, (await _w.Git.GetAppAsync(AppA))!.Owner);
        Assert.Null(_w.Ledger.PeekFirstRun(TaskItem("t1")));
    }

    [Fact]
    public async Task A_key_the_old_owner_or_an_inactive_person_cannot_be_the_new_owner()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        _w.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", Enabled = true, PermissionLevel = "admin" });
        var handover = _w.Handover();

        Assert.NotNull((await handover.CheckAsync(GoneId, "key:k1", [TaskItem("t1")])).OwnerRefused);
        Assert.NotNull((await handover.CheckAsync(GoneId, GoneId, [TaskItem("t1")])).OwnerRefused);
        Assert.NotNull((await handover.CheckAsync(GoneId, PrincipalIds.User("nobody@example.com"), [TaskItem("t1")])).OwnerRefused);
        Assert.Null((await handover.CheckAsync(GoneId, NewId, [TaskItem("t1")])).OwnerRefused);
    }

    [Fact]
    public async Task The_new_owner_check_refuses_on_resolver_exception_and_unknown_capability()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId, action: "frobnicate");
        var real = _w.Resolver;
        _w.Resolver = new ThrowingResolver(real, NewId);

        Assert.Contains("could not be read", (await _w.Handover().CheckAsync(GoneId, NewId, [TaskItem("t1")])).OwnerRefused);

        _w.Resolver = real;
        var check = await _w.Handover().CheckAsync(GoneId, NewId, [TaskItem("t1"), Hook("h1")]);
        Assert.Contains("unknown capability", check.Refused[Hook("h1")]);
        Assert.DoesNotContain(TaskItem("t1"), check.Refused.Keys);
    }

    [Fact]
    public async Task A_key_owned_task_or_webhook_and_a_key_created_task_are_refused()
    {
        await _w.SeedTaskAsync("keyowned", "key:k-gone");
        await _w.SeedWebhookAsync("h1", "key:k-gone");
        await _w.SeedTaskAsync("keymade", GoneId, via: "key:k-gone");

        Assert.All(await _w.Handover().OwnedByAsync("key:k-gone"), i => Assert.True(i.KeyProvenance));
        Assert.True(Assert.Single(await _w.Handover().OwnedByAsync(GoneId)).KeyProvenance);
        var check = await _w.Handover().CheckAsync("key:k-gone", NewId, [TaskItem("keyowned"), Hook("h1")]);
        Assert.Equal(2, check.Refused.Count);
        Assert.Contains("MCP key", (await _w.Handover().CheckAsync(GoneId, NewId, [TaskItem("keymade")])).Refused[TaskItem("keymade")]);
        await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(GoneId, NewId, TaskItem("keymade")));
        Assert.Equal(GoneId, (await _w.TaskAsync("keymade")).CreatedBy);
    }

    [Fact]
    public async Task An_unreadable_account_record_refuses_the_handover()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        _w.Accounts.Unreadable = true;

        await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(GoneId, NewId, TaskItem("t1")));

        _w.Accounts.Unreadable = false;
        Assert.Equal(GoneId, (await _w.TaskAsync("t1")).CreatedBy);
    }

    [Fact]
    public async Task An_item_taken_over_meanwhile_is_skipped_and_the_rest_proceed()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", GoneId);
        // The page showed both as Gone's; t1 is taken over (as today) before the click.
        Assert.True(await _w.Scheduler.SetOwnerAsync("t1", StillId, GoneId));

        var result = await HandOverAsync(GoneId, NewId, TaskItem("t1"), TaskItem("t2"));

        Assert.Equal(HandoverItemStatus.SkippedOwnerChanged, result.Items.Single(i => i.Ref.Id == "t1").Status);
        Assert.Equal(HandoverItemStatus.HandedOver, result.Items.Single(i => i.Ref.Id == "t2").Status);
        Assert.Equal(StillId, (await _w.TaskAsync("t1")).CreatedBy);
        Assert.Equal(NewId, (await _w.TaskAsync("t2")).CreatedBy);
    }

    [Fact]
    public async Task A_deleted_item_is_skipped()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await _w.Scheduler.DeleteTaskAsync("t1");

        var result = await HandOverAsync(GoneId, NewId, TaskItem("t1"), Hook("h1"));

        Assert.Equal(HandoverItemStatus.SkippedGone, result.Items.Single(i => i.Ref.Id == "t1").Status);
        Assert.Equal(HandoverItemStatus.HandedOver, result.Items.Single(i => i.Ref.Id == "h1").Status);
        Assert.Null(_w.Ledger.PeekFirstRun(TaskItem("t1")));
    }

    [Fact]
    public async Task A_store_failure_half_way_reports_failed_and_not_attempted_and_leaves_written_items_off()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, GoneId);
        var check = await _w.SeedCheckAsync(GoneId);
        Break(_w.Paths.AutomationOwnersJson);   // the fleet feature's write fails

        using var scope = Decision("automation.handover");
        await Assert.ThrowsAnyAsync<Exception>(() => _w.Handover().HandOverAsync(GoneId, NewId,
            [TaskItem("t1"), Feature(IAutomationOwnerStore.AutoPatch), Check(check.Id)]));

        var details = string.Join("\n", scope.Results.Select(r => r.Details));
        Assert.Contains("status: HandedOver", details);
        Assert.Contains("status: Failed", details);
        Assert.Contains("status: NotAttempted", details);
        Assert.NotNull(scope.FailureReason);
        Assert.Equal((NewId, false), ((await _w.TaskAsync("t1")).CreatedBy, (await _w.TaskAsync("t1")).Enabled));
        Assert.Equal(GoneId, _w.Checks.List().Single().OwnerId);
        // The ledger keeps only what was handed over.
        Assert.NotNull(_w.Ledger.PeekFirstRun(TaskItem("t1")));
        Assert.Null(_w.Ledger.PeekFirstRun(Check(check.Id)));
    }

    [Fact]
    public async Task A_recheck_failure_inside_the_delegate_writes_nothing_and_records_failed()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        var handover = _w.Handover();
        Assert.True((await handover.CheckAsync(GoneId, NewId, [TaskItem("t1")])).Ok);   // what the page saw
        _w.Roles.Map[NewOwner] = AppRole.Viewer;   // demoted before the click

        using var scope = Decision("automation.handover");
        await Assert.ThrowsAsync<HandoverRefusedException>(() => handover.HandOverAsync(GoneId, NewId, [TaskItem("t1")]));

        Assert.Contains(scope.Results, r => !r.Success && r.Details?.Contains("re-check") == true);
        Assert.NotNull(scope.FailureReason);
        Assert.Equal(GoneId, (await _w.TaskAsync("t1")).CreatedBy);
    }

    [Fact]
    public async Task A_suspension_racing_the_handover_is_superseded()
    {
        var check = await _w.SeedCheckAsync(GoneId);

        await HandOverAsync(GoneId, NewId, Check(check.Id));
        // A run started before the handover suspends the check for its old owner: the compare-and-set misses.
        Assert.False(await _w.Checks.SuspendAsync(check.Id, GoneId, "owner lost"));

        Assert.Equal(NewId, _w.Checks.List().Single().OwnerId);
    }

    [Fact]
    public async Task Scheduled_task_provenance_is_unchanged_and_an_agent_created_task_keeps_the_agent_rule_set()
    {
        await _w.SeedTaskAsync("t1", GoneId, via: "agent:s1");

        await HandOverAsync(GoneId, NewId, TaskItem("t1"));

        var task = await _w.TaskAsync("t1");
        Assert.Equal("agent:s1", task.CreatedVia);
        // A run keeps the agent as its delegate: the agent rule set still applies (D5).
        await RunTaskAsync(_w.Runner, task);
        Assert.Equal("agent:s1", _w.Audit.Principals.Last().DelegatedBy);
        Assert.Equal(NewId, _w.Audit.Principals.Last().OnBehalfOf?.Id);
    }

    [Fact]
    public async Task A_handed_over_git_app_is_held_and_its_webhook_deployment_is_refused_as_AutomationHeld()
    {
        await _w.SeedAppAsync(AppA, GoneId);

        await HandOverAsync(GoneId, NewId, App(AppA));
        var (success, output, refused) = await GitDeployOwners.RunAsOwnerAsync(_w.Git, _w.Runner, AppA);

        Assert.False(success);
        Assert.True(refused);
        Assert.StartsWith(MutationReasonCodes.AutomationHeld, output);
        Assert.Equal("denied:Denied", _w.Audit.Events.LastOrDefault());   // on record as the gateway's Denied gitdeploy.deploy
    }

    // ---- resume ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resume_never_re_owns_for_each_kind()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await _w.SeedAppAsync(AppA, GoneId);
        await _w.SeedTriggerAsync("g1", GoneId);
        var check = await _w.SeedCheckAsync(GoneId);
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"), Hook("h1"), App(AppA), Trigger("g1"), Check(check.Id),
            Feature(IAutomationOwnerStore.AutoPatch));

        // Resumed by another Admin than the new owner.
        using (Decision("scheduler.update"))
        {
            var task = await _w.TaskAsync("t1");
            task.Enabled = true;
            await _w.Scheduler.UpdateTaskAsync(task);
        }
        using (Decision("webhook.toggle")) await _w.Webhooks.SetEnabledAsync("h1", true);
        using (Decision("gitdeploy.save")) Assert.True(await _w.Git.ResumeAsync(AppA, NewId));
        using (Decision("aitrigger.save"))
        {
            var trigger = AiTriggerCopy(_w.Triggers.Triggers[0]);
            trigger.Enabled = true;
            await _w.Triggers.SaveAsync([trigger], Editor(Admin), _w.Triggers.Revision);
        }
        using (Decision("endpointcheck.save")) Assert.True(await _w.Checks.ResumeAsync(check.Id, NewId, default));
        using (Decision("automation.owner_set")) Assert.True(await _w.Fleet.ResumeAsync(IAutomationOwnerStore.AutoPatch, NewId));

        Assert.Equal((NewId, true), ((await _w.TaskAsync("t1")).CreatedBy, (await _w.TaskAsync("t1")).Enabled));
        Assert.Equal((NewId, true), ((await _w.WebhookAsync("h1")).Owner, (await _w.WebhookAsync("h1")).Enabled));
        var app = (await _w.Git.GetAppAsync(AppA))!;
        Assert.Equal((NewId, (string?)null), (app.Owner, app.HeldReason));
        Assert.Equal((NewId, true), (_w.Triggers.Triggers[0].OwnerId, _w.Triggers.Triggers[0].Enabled));
        Assert.Equal((NewId, true), (_w.Checks.List().Single().OwnerId, _w.Checks.List().Single().Enabled));
        Assert.Equal(NewId, _w.Fleet.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
        foreach (var item in new[] { TaskItem("t1"), Hook("h1"), App(AppA), Trigger("g1"), Check(check.Id), Feature(IAutomationOwnerStore.AutoPatch) })
            Assert.Equal(AdminId, _w.Ledger.PeekFirstRun(item)?.ResumedBy);
    }

    [Fact]
    public async Task Resume_is_refused_when_the_recorded_owner_cannot_run_the_item()
    {
        // A hold keeps the old owner: the page's resume check finds it cannot run, and a run switched on anyway is refused.
        await _w.SeedTaskAsync("t1", GoneId);
        using (Decision("account.create")) await _w.Hold().HoldAllAsync(GoneId);

        var item = Assert.Single(await _w.Handover().OwnedByAsync(GoneId));
        Assert.Equal(HandoverEligibility.CannotRunOnOwner, item.Eligibility);
        Assert.False(item.On);

        var run = await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"));
        Assert.Equal(MutationStatus.Denied, run.Status);
    }

    [Fact]
    public async Task The_first_run_after_resume_names_the_handover_and_the_resumer()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        using (Decision("scheduler.update", StillId, McpPermissionLevels.Write))
        {
            var task = await _w.TaskAsync("t1");
            task.Enabled = true;
            await _w.Scheduler.UpdateTaskAsync(task);
        }

        IReadOnlyList<MutationDomainResult> first = [], second = [];
        await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"), r => first = r);
        await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"), r => second = r);

        var note = Assert.Single(first, r => r.Details?.StartsWith("handover = ") == true).Details!;
        Assert.Contains($"from: {GoneId}", note);
        Assert.Contains($"to: {NewId}", note);
        Assert.Contains($"handedOverBy: {AdminId}", note);
        Assert.Contains($"resumedBy: {StillId}", note);
        Assert.DoesNotContain(second, r => r.Details?.StartsWith("handover = ") == true);
    }

    [Fact]
    public async Task A_returning_sso_sign_in_never_regains_handed_over_items()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"), Hook("h1"));

        // The old owner signs in again by SSO: allowed, with their old role.
        _w.Accounts.Deleted.Remove(Gone);
        _w.Whitelist.Allowed.Add(Gone);
        _w.Roles.Map[Gone] = AppRole.Admin;

        Assert.Empty(await _w.Handover().OwnedByAsync(GoneId));
        Assert.Equal(NewId, (await _w.TaskAsync("t1")).CreatedBy);
        Assert.Equal(NewId, (await _w.WebhookAsync("h1")).Owner);
        Assert.False((await _w.TaskAsync("t1")).Enabled);
    }

    // ---- the record ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_event_names_old_and_new_owner_with_authority_and_every_item()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId, enabled: false);

        using var scope = Decision("automation.handover");
        await _w.Handover().HandOverAsync(GoneId, NewId, [TaskItem("t1"), Hook("h1")]);

        var details = Assert.Single(scope.Results, r => r.Details?.StartsWith("handover = ") == true).Details!;
        Assert.Contains($"from: {GoneId} (AccountDeleted", details);
        Assert.Contains($"to: {NewId} (Active, {McpPermissionLevels.Admin})", details);
        Assert.Contains("items: 2", details);
        // Every item is its own result, so none is cut at the audit's per-result length.
        var items = scope.Results.Where(r => r.TargetType == "automation-item").ToDictionary(r => r.TargetId, r => r.Details);
        Assert.Equal("kind: ScheduledTask, id: t1, name: task t1, wasOn: true, status: HandedOver", items["ScheduledTask:t1"]);
        Assert.Equal("kind: Webhook, id: h1, name: hook h1, wasOn: false, status: HandedOver", items["Webhook:h1"]);
    }

    [Fact]
    public async Task The_admin_notification_names_counts_only_and_the_new_owner_gets_a_personal_notice_until_acknowledged()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", GoneId);

        await HandOverAsync(GoneId, NewId, TaskItem("t1"), TaskItem("t2"));

        var sent = Assert.Single(_w.Notify.Events);
        Assert.Equal(NotificationEventTypes.AutomationHandedOver, sent.EventType);
        Assert.Contains("2 items: 2 ScheduledTask", sent.ImageInfo);
        Assert.DoesNotContain("task t1", sent.ImageInfo);
        var notice = Assert.Single(_w.Notices.For(NewId));
        Assert.Equal(2, notice.Count);
        Assert.Empty(_w.Notices.For(AdminId));

        using (Decision("automation.handover_acknowledge", NewId, McpPermissionLevels.Read))
            await _w.Notices.AcknowledgeAsync(notice.Id);
        Assert.Empty(_w.Notices.For(NewId));
    }

    [Fact]
    public async Task The_ledger_is_written_before_any_item_and_a_ledger_failure_changes_nothing()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        Break(_w.Paths.AutomationHandoversJson);

        var refused = await Assert.ThrowsAsync<HandoverRefusedException>(() => HandOverAsync(GoneId, NewId, TaskItem("t1")));

        Assert.Contains("ledger", refused.Message);
        Assert.Equal((GoneId, true), ((await _w.TaskAsync("t1")).CreatedBy, (await _w.TaskAsync("t1")).Enabled));
        Assert.Empty(_w.Notify.Events);
    }

    [Fact]
    public async Task An_item_failure_after_the_ledger_write_is_reconciled_as_failed()
    {
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, GoneId);
        Break(_w.Paths.AutomationOwnersJson);

        using (Decision("automation.handover"))
            await Assert.ThrowsAnyAsync<Exception>(() =>
                _w.Handover().HandOverAsync(GoneId, NewId, [Feature(IAutomationOwnerStore.AutoPatch)]));

        // Written as Pending before the item; reconciled as Failed, so no first-run note claims a handover.
        Assert.Null(_w.Ledger.PeekFirstRun(Feature(IAutomationOwnerStore.AutoPatch)));
    }

    [Fact]
    public async Task A_denied_first_run_keeps_the_ledger_entry_and_the_first_executed_run_consumes_it()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        _w.Roles.Map[NewOwner] = AppRole.Viewer;

        var denied = await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"));
        Assert.Equal(MutationStatus.Denied, denied.Status);
        Assert.NotNull(_w.Ledger.PeekFirstRun(TaskItem("t1")));

        _w.Roles.Map[NewOwner] = AppRole.Admin;
        var executed = await RunTaskAsync(_w.Runner, await _w.TaskAsync("t1"));
        Assert.Equal(MutationStatus.Executed, executed.Status);
        Assert.Null(_w.Ledger.PeekFirstRun(TaskItem("t1")));
    }

    [Fact]
    public async Task Deleting_a_handed_over_item_forgets_its_entry_and_unresumed_entries_age_out_after_30_days()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"), Hook("h1"));

        await _w.Scheduler.DeleteTaskAsync("t1");
        Assert.Null(_w.Ledger.PeekFirstRun(TaskItem("t1")));

        var handedOverAt = _w.Ledger.PeekFirstRun(Hook("h1"))!.HandedOverAtUtc;
        Assert.Equal(0, _w.Ledger.Prune(HandoverNotes.MaxAge, handedOverAt.AddDays(29)));
        Assert.NotNull(_w.Ledger.PeekFirstRun(Hook("h1")));
        Assert.Equal(1, _w.Ledger.Prune(HandoverNotes.MaxAge, handedOverAt.AddDays(31)));
        Assert.Null(_w.Ledger.PeekFirstRun(Hook("h1")));
        Assert.Equal(TimeSpan.FromDays(30), HandoverNotes.MaxAge);
    }

    // ---- the notice ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Acknowledge_is_refused_for_another_persons_notice_and_passes_the_kill_switch()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        var notice = Assert.Single(_w.Notices.For(NewId));

        using (Decision("automation.handover_acknowledge", StillId, McpPermissionLevels.Read))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _w.Notices.AcknowledgeAsync(notice.Id));
        Assert.Single(_w.Notices.For(NewId));

        // Read level and reversible: the kill switch stops writes, not a person's own bookkeeping.
        var capability = new CapabilityCatalog().Find("automation.handover_acknowledge")!;
        Assert.Equal(McpPermissionLevels.Read, capability.RequiredLevel);
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationGateway>.Instance,
            targets: TestTargets.Resolver());
        var page = new UiMutationRunner(gateway, new CapabilityCatalog(), new Person(NewOwner));
        await page.RunAsync("automation.handover_acknowledge", new { notice = notice.Id },
            (ResolvedRecord rt, CancellationToken ct) => _w.Notices.AcknowledgeAsync(rt.Id, ct));
        Assert.Empty(_w.Notices.For(NewId));
    }

    [Fact]
    public void The_acknowledge_capability_is_read_level_reversible_on_handover_notice()
    {
        var catalog = new CapabilityCatalog();
        var acknowledge = catalog.Find("automation.handover_acknowledge")!;
        Assert.Equal(McpPermissionLevels.Read, acknowledge.RequiredLevel);
        Assert.Equal(CapabilityRisk.Reversible, acknowledge.Risk);
        Assert.Equal("handover-notice", acknowledge.TargetKind);
        var handover = catalog.Find("automation.handover")!;
        Assert.Equal(McpPermissionLevels.Admin, handover.RequiredLevel);
        Assert.Equal("automation-owner", handover.TargetKind);
        Assert.Equal("automation-owner", catalog.Find("automation.hold")!.TargetKind);
    }

    // ---- D11 (c): account.create holds before the identity row exists ----------------------------------------------

    [Fact]
    public async Task Creating_an_account_for_an_address_with_owned_items_holds_them_before_the_identity_row_exists()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        const string returning = "back@local.test";
        await _w.SeedTaskAsync("t1", PrincipalIds.User(returning));
        var hold = new ProbingHold(_w.Hold(), () => world.FindAsync(returning).GetAwaiter().GetResult() is null);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: hold,
            ownerResolver: Resolver(world));

        var created = await admin.Mutations.RunAsync("account.create", new { account = returning, role = "Operator" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(returning, AppRole.Operator, ct));

        Assert.True(created.Success, created.ToString());
        Assert.Equal([true], hold.RowAbsentAtHold);
        var task = await _w.TaskAsync("t1");
        Assert.Equal((PrincipalIds.User(returning), false), (task.CreatedBy, task.Enabled));
        Assert.Equal(HandoverEntryKind.Hold, _w.Ledger.PeekFirstRun(TaskItem("t1"))?.Kind);
    }

    [Fact]
    public async Task A_hold_failure_creates_no_account()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        const string returning = "back@local.test";
        await _w.SeedTaskAsync("t1", PrincipalIds.User(returning));
        Break(_w.Paths.AutomationHandoversJson);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: _w.Hold(),
            ownerResolver: Resolver(world));

        await Assert.ThrowsAnyAsync<Exception>(() => admin.Mutations.RunAsync("account.create",
            new { account = returning, role = "Operator" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(returning, AppRole.Operator, ct)));

        Assert.Null(await world.FindAsync(returning));
        Assert.False(world.Roles.HasExplicitRole(returning));
    }

    [Fact]
    public async Task Creating_a_local_account_for_an_active_federated_user_holds_nothing()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        const string fed = "fed@local.test";
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root, fed] });
        await world.Roles.SetRoleAsync(fed, AppRole.Operator);
        await _w.SeedTaskAsync("t1", PrincipalIds.User(fed));
        var hold = new ProbingHold(_w.Hold(), () => true);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: hold,
            ownerResolver: Resolver(world, new AutomationMutationRunnerTests.Accounts()));

        var created = await admin.Mutations.RunAsync("account.create", new { account = fed, role = "Operator" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(fed, AppRole.Operator, ct));

        Assert.True(created.Success, created.ToString());
        Assert.Empty(hold.RowAbsentAtHold);
        Assert.True((await _w.TaskAsync("t1")).Enabled);
    }

    [Fact]
    public async Task Creating_a_local_account_for_a_federated_user_removed_from_the_whitelist_holds_their_items()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        const string fed = "fed@local.test";
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        await _w.SeedTaskAsync("t1", PrincipalIds.User(fed));
        await _w.SeedWebhookAsync("h1", PrincipalIds.User(fed));
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: _w.Hold(),
            ownerResolver: Resolver(world));

        var created = await admin.Mutations.RunAsync("account.create", new { account = fed, role = "Viewer" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(fed, AppRole.Viewer, ct));

        Assert.True(created.Success, created.ToString());
        Assert.False((await _w.TaskAsync("t1")).Enabled);
        Assert.False((await _w.WebhookAsync("h1")).Enabled);
        Assert.Equal(PrincipalIds.User(fed), (await _w.WebhookAsync("h1")).Owner);
    }

    [Fact]
    public async Task A_refused_account_create_holds_nothing()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        await _w.SeedTaskAsync("t1", PrincipalIds.User(LocalAccountWorld.Root));
        var hold = new ProbingHold(_w.Hold(), () => true);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: hold,
            ownerResolver: Resolver(world));

        // The account exists already: refused before the hold.
        var refused = await admin.Accounts.CreateAsync(LocalAccountWorld.Root, AppRole.Admin);

        Assert.Equal(LocalAccountErrors.AlreadyExists, refused.Error);
        Assert.Empty(hold.RowAbsentAtHold);
        Assert.True((await _w.TaskAsync("t1")).Enabled);
    }

    [Fact]
    public async Task A_rolled_back_account_create_leaves_the_items_held_and_nothing_runs()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        await world.DropTableAsync("AspNetUserTokens");   // the invitation cannot be issued: the account is undone
        const string returning = "back@local.test";
        await _w.SeedTaskAsync("t1", PrincipalIds.User(returning));
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), hold: _w.Hold(),
            ownerResolver: Resolver(world));

        await Assert.ThrowsAnyAsync<Exception>(() => admin.Mutations.RunAsync("account.create",
            new { account = returning, role = "Operator" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(returning, AppRole.Operator, ct)));

        Assert.Null(await world.FindAsync(returning));
        var task = await _w.TaskAsync("t1");
        Assert.False(task.Enabled);   // held: fail safe, an Admin resumes it
        Assert.Equal(HandoverEntryKind.Hold, _w.Ledger.PeekFirstRun(TaskItem("t1"))?.Kind);
    }

    [Fact]
    public async Task A_held_item_resumed_after_re_creation_keeps_the_re_created_owner_and_names_the_resumer()
    {
        const string returning = "back@local.test";
        var ownerId = PrincipalIds.User(returning);
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, ownerId);
        using (Decision("account.create")) await _w.Hold().HoldAllAsync(ownerId);
        Assert.Null(_w.Fleet.Get(IAutomationOwnerStore.AutoPatch));

        // The account exists again (Active); an Admin resumes the feature for its recorded owner.
        _w.Whitelist.Allowed.Add(returning);
        _w.Roles.Map[returning] = AppRole.Admin;
        using (Decision("automation.owner_set")) Assert.True(await _w.Fleet.ResumeAsync(IAutomationOwnerStore.AutoPatch, ownerId));

        Assert.Equal(ownerId, _w.Fleet.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
        var entry = _w.Ledger.PeekFirstRun(Feature(IAutomationOwnerStore.AutoPatch))!;
        Assert.Equal(HandoverEntryKind.Hold, entry.Kind);
        Assert.Equal(AdminId, entry.ResumedBy);
        Assert.Contains($"resumedBy: {AdminId}", HandoverNotes.Of(entry));
        Assert.StartsWith("hold = ", HandoverNotes.Of(entry));
    }

    // ---- review round 1: a held item stays held on every path -------------------------------------------------------

    private static readonly string BackId = PrincipalIds.User("back@local.test");

    [Fact]
    public async Task An_mcp_token_change_leaves_a_handed_over_git_app_held()
    {
        await _w.SeedAppAsync(AppA, GoneId);
        await HandOverAsync(GoneId, NewId, App(AppA));
        var before = (await _w.Git.GetAppAsync(AppA))!;
        var (http, permissions) = await McpAdminAsync();
        // Set here: the accessor keeps the context in an AsyncLocal, which an awaited helper would not hand back.
        var accessor = new HttpContextAccessor { HttpContext = http };

        using (Decision("gitdeploy.set_token"))
            await Whiskers.Mcp.Tools.GitDeployTools.SetGitDeployToken(accessor, permissions, _w.Git, _w.Webhooks, AppA,
                before.Revision, "");

        var after = (await _w.Git.GetAppAsync(AppA))!;
        Assert.Equal(before.Revision + 1, after.Revision);   // the token change was saved
        Assert.Equal(GitDeployService.HandoverHeldReason, after.HeldReason);
        var (success, output, refused) = await GitDeployOwners.RunAsOwnerAsync(_w.Git, _w.Runner, AppA);
        Assert.False(success);
        Assert.True(refused);
        Assert.StartsWith(MutationReasonCodes.AutomationHeld, output);
    }

    [Fact]
    public async Task A_save_off_the_page_leaves_a_held_git_app_held_and_only_the_pages_save_releases_it()
    {
        await _w.SeedAppAsync(AppA, GoneId);
        await HandOverAsync(GoneId, NewId, App(AppA));

        // A save as the MCP tools make it (no release), from a copy that claims no hold: the stored hold stands.
        var copy = (await _w.Git.GetAppAsync(AppA))!;
        copy.HeldReason = null;
        using (Decision("gitdeploy.save")) await _w.Git.SaveAppAsync(copy, null, NewId);
        var kept = (await _w.Git.GetAppAsync(AppA))!;
        Assert.Equal(GitDeployService.HandoverHeldReason, kept.HeldReason);
        Assert.Null(_w.Ledger.PeekFirstRun(App(AppA))!.ResumedBy);   // no resume recorded

        // The page's save releases it, as the contract accepts there; that is its resume.
        using (Decision("gitdeploy.save")) await _w.Git.SaveAppAsync(kept, null, NewId, releaseHold: true);
        Assert.Null((await _w.Git.GetAppAsync(AppA))!.HeldReason);
        Assert.Equal(AdminId, _w.Ledger.PeekFirstRun(App(AppA))?.ResumedBy);
    }

    [Fact]
    public async Task Run_now_of_a_handed_over_or_held_task_is_refused_as_AutomationHeld_until_it_is_switched_on()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", BackId);
        await _w.SeedTaskAsync("t3", NewId, enabled: false);   // switched off by hand: no ledger entry
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        using (Decision("account.create")) await _w.Hold().HoldAllAsync(BackId);
        var executor = new CountingExecutor();
        var scheduler = _w.SchedulerRunning(executor);

        foreach (var id in new[] { "t1", "t2" })
        {
            using var scope = Decision("scheduler.run");
            var (success, output) = await scheduler.RunNowAsync(id);
            Assert.False(success);
            Assert.StartsWith(MutationReasonCodes.AutomationHeld, output);
            Assert.StartsWith(MutationReasonCodes.AutomationHeld, scope.FailureReason);
        }
        Assert.Equal(0, executor.Runs);
        Assert.Empty(await scheduler.GetHistoryAsync("t1"));

        // A task that is off for another reason runs by hand as today.
        using (Decision("scheduler.run")) Assert.True((await scheduler.RunNowAsync("t3")).Success);
        // Switched on (its resume): "Run now" runs it.
        using (Decision("scheduler.update"))
        {
            var task = await _w.TaskAsync("t1");
            task.Enabled = true;
            await scheduler.UpdateTaskAsync(task);
        }
        using (Decision("scheduler.run")) Assert.True((await scheduler.RunNowAsync("t1")).Success);
        Assert.Equal(2, executor.Runs);
    }

    [Fact]
    public async Task Run_now_of_a_handed_over_task_stays_refused_after_the_30_day_ageing()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        var handedOverAt = _w.Ledger.PeekFirstRun(TaskItem("t1"))!.HandedOverAtUtc;

        // Amendment point 15: a scheduled task's unresumed entry does not age out.
        Assert.Equal(0, _w.Ledger.Prune(HandoverNotes.MaxAge, handedOverAt.AddDays(90)));
        Assert.True(_w.Ledger.AwaitsResume(TaskItem("t1")));

        var executor = new CountingExecutor();
        var scheduler = _w.SchedulerRunning(executor);
        using (var scope = Decision("scheduler.run"))
        {
            var (success, output) = await scheduler.RunNowAsync("t1");
            Assert.False(success);
            Assert.StartsWith(MutationReasonCodes.AutomationHeld, output);
        }
        Assert.Equal(0, executor.Runs);
    }

    [Fact]
    public async Task Run_now_of_an_off_task_is_refused_while_the_ledger_cannot_be_read()
    {
        await _w.SeedTaskAsync("t1", NewId, enabled: false);
        await _w.SeedTaskAsync("t2", NewId);
        Break(_w.Paths.AutomationHandoversJson);
        var executor = new CountingExecutor();
        var scheduler = _w.SchedulerRunning(executor);

        using (Decision("scheduler.run"))
        {
            var (success, output) = await scheduler.RunNowAsync("t1");
            Assert.False(success);
            Assert.StartsWith(MutationReasonCodes.AutomationHeld, output);
            Assert.Contains("ledger", output);
        }
        // A task that is on was never handed over or was resumed: the ledger is not needed to run it.
        using (Decision("scheduler.run")) Assert.True((await scheduler.RunNowAsync("t2")).Success);
        Assert.Equal(1, executor.Runs);
    }

    [Fact]
    public async Task The_mcp_run_task_of_a_handed_over_task_is_refused_as_AutomationHeld()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await HandOverAsync(GoneId, NewId, TaskItem("t1"));
        var executor = new CountingExecutor();
        var scheduler = _w.SchedulerRunning(executor);
        var (http, permissions) = await McpAdminAsync(new ServiceCollection()
            .AddSingleton<ISchedulerService>(scheduler)
            .AddSingleton<IEffectiveRequirementResolver>(sp => new SchedulerRequirementResolver(new CapabilityCatalog(), sp))
            .BuildServiceProvider());
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var scope = Decision("scheduler.run");
        scope.DecidedLevel = McpPermissionLevels.Admin;

        var answer = await Whiskers.Mcp.Tools.SchedulerTools.RunScheduledTask(accessor, permissions, scheduler,
            new NullToolAudit(), "t1");

        Assert.Contains(MutationReasonCodes.AutomationHeld, answer);
        Assert.StartsWith(MutationReasonCodes.AutomationHeld, scope.FailureReason);
        Assert.Equal(0, executor.Runs);
    }

    [Fact]
    public void A_ledger_write_keeps_a_young_pending_row_of_another_operation_and_drops_a_stale_one()
    {
        var path = _w.Paths.AutomationHandoversJson;
        var stale = new HandoverLedger.Row(AutomationKind.ScheduledTask, "old", true,
            new HandoverEntry(HandoverEntryKind.Handover, "op-old", GoneId, NewId, AdminId, DateTime.UtcNow.AddHours(-2), null, null));
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { stale },
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));

        // A hold has written its rows (Pending) when a handover writes its own.
        _w.Ledger.RecordHandover("op-hold", BackId, BackId, AdminId, [TaskItem("held")], HandoverEntryKind.Hold);
        _w.Ledger.RecordHandover("op-hand", GoneId, NewId, AdminId, [TaskItem("handed")]);
        _w.Ledger.Reconcile("op-hold", TaskItem("held"), HandoverItemStatus.HandedOver);
        _w.Ledger.Reconcile("op-hand", TaskItem("handed"), HandoverItemStatus.HandedOver);

        Assert.Equal(HandoverEntryKind.Hold, _w.Ledger.PeekFirstRun(TaskItem("held"))?.Kind);
        Assert.Equal(HandoverEntryKind.Handover, _w.Ledger.PeekFirstRun(TaskItem("handed"))?.Kind);
        Assert.DoesNotContain("op-old", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_hold_and_a_handover_share_one_lock_and_both_keep_their_ledger_rows()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", BackId);
        async Task<IReadOnlyList<HandoverItemResult>> HoldAsync()
        {
            using var scope = Decision("account.create");
            return await _w.Hold().HoldAllAsync(BackId);
        }
        async Task<HandoverResult> HandAsync()
        {
            using var scope = Decision("automation.handover");
            return await _w.Handover().HandOverAsync(GoneId, NewId, [TaskItem("t1")]);
        }

        var gate = HandoverLocks.For(_w.Ledger);
        await gate.WaitAsync();   // another handover is running
        System.Threading.Tasks.Task hold, handover;
        try
        {
            hold = HoldAsync();
            handover = HandAsync();
            await System.Threading.Tasks.Task.Delay(200);
            Assert.False(hold.IsCompleted);
            Assert.False(handover.IsCompleted);
        }
        finally
        {
            gate.Release();
        }
        await System.Threading.Tasks.Task.WhenAll(hold, handover);

        Assert.Equal(HandoverEntryKind.Hold, _w.Ledger.PeekFirstRun(TaskItem("t2"))?.Kind);
        Assert.Equal(HandoverEntryKind.Handover, _w.Ledger.PeekFirstRun(TaskItem("t1"))?.Kind);
    }

    [Fact]
    public async Task A_handover_that_fails_half_way_still_notifies_the_new_owner_and_the_admins_of_what_was_handed_over()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedFleetAsync(IAutomationOwnerStore.AutoPatch, GoneId);
        Break(_w.Paths.AutomationOwnersJson);   // the fleet feature's write fails after the task's

        using (Decision("automation.handover"))
            await Assert.ThrowsAnyAsync<Exception>(() => _w.Handover().HandOverAsync(GoneId, NewId,
                [TaskItem("t1"), Feature(IAutomationOwnerStore.AutoPatch)]));

        Assert.Equal(1, Assert.Single(_w.Notices.For(NewId)).Count);
        Assert.Contains("1 items: 1 ScheduledTask", Assert.Single(_w.Notify.Events).ImageInfo);
    }

    [Fact]
    public async Task Resume_switches_on_only_a_check_that_a_handover_or_hold_switched_off()
    {
        var check = await _w.SeedCheckAsync(NewId);
        Assert.True(await _w.Checks.SuspendAsync(check.Id, NewId, "owner lost"));

        using (Decision("endpointcheck.save")) Assert.False(await _w.Checks.ResumeAsync(check.Id, NewId, default));

        Assert.False(_w.Checks.List().Single().Enabled);
    }

    [Fact]
    public async Task A_hold_matches_the_owner_address_without_regard_to_case()
    {
        const string stored = "user:Back@Local.Test";
        await _w.SeedTaskAsync("t1", stored);

        IReadOnlyList<HandoverItemResult> held;
        using (Decision("account.create")) held = await _w.Hold().HoldAllAsync(BackId);

        Assert.Equal(HandoverItemStatus.HandedOver, Assert.Single(held).Status);
        var task = await _w.TaskAsync("t1");
        Assert.Equal((stored, false), (task.CreatedBy, task.Enabled));
    }

    [Fact]
    public async Task Without_the_hold_service_no_account_is_created()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        const string returning = "back@local.test";
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), ownerResolver: Resolver(world),
            withoutHold: true);

        await Assert.ThrowsAnyAsync<Exception>(() => admin.Mutations.RunAsync("account.create",
            new { account = returning, role = "Operator" },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(returning, AppRole.Operator, ct)));

        Assert.Null(await world.FindAsync(returning));
    }

    /// <summary>An MCP caller with an Admin key over the world's data directory; the caller sets the accessor itself.</summary>
    private async Task<(DefaultHttpContext Http, IMcpPermissionService Permissions)> McpAdminAsync(IServiceProvider? services = null)
    {
        var permissions = new McpPermissionService(NullLogger<McpPermissionService>.Instance, _w.Paths);
        await permissions.InitializeAsync();
        var key = await permissions.AddApiKeyAsync("admin", McpPermissionLevels.Admin);
        var http = new DefaultHttpContext { RequestServices = services ?? new ServiceCollection().BuildServiceProvider() };
        http.Request.Headers.Authorization = "Bearer " + key.Key;
        return (http, permissions);
    }

    // ---- doubles ---------------------------------------------------------------------------------------------------

    /// <summary>"Run now" without a host: counts the runs it was handed.</summary>
    private sealed class CountingExecutor : ITaskExecutor
    {
        public int Runs { get; private set; }

        public Task<(bool Success, string Output)> ExecuteAsync(ScheduledTaskEntity task)
        {
            Runs++;
            return System.Threading.Tasks.Task.FromResult((true, "ran"));
        }
    }

    private sealed class NullToolAudit : Whiskers.Services.AuditLog.IToolAuditLog
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>The owner resolver the account service asks; <paramref name="accounts"/> replaces the world's account record
    /// (a federated sign-in the world has no provider for).</summary>
    private IAutomationOwnerResolver Resolver(LocalAccountWorld world, IOwnerAccounts? accounts = null) =>
        new AutomationOwnerResolver(world.Roles, world.Whitelist, _w.Keys, accounts ?? world.Owners);

    private static Whiskers.Models.AiTrigger AiTriggerCopy(Whiskers.Models.AiTrigger t) =>
        Whiskers.Services.Agent.Triggers.AiTriggerStore.Normalized(t);

    private sealed class Person(string email) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => System.Threading.Tasks.Task.FromResult<string?>(email);
        public Task<AppRole> GetRoleAsync() => System.Threading.Tasks.Task.FromResult(AppRole.Viewer);
        public Task<bool> HasRoleAsync(AppRole required) => System.Threading.Tasks.Task.FromResult(required <= AppRole.Viewer);
    }

    /// <summary>The interface default <c>LostAuthority</c>: it cannot judge anything.</summary>
    private sealed class DefaultRunner : IAutomationMutationRunner;

    /// <summary>Throws for one owner id, delegates the rest.</summary>
    private sealed class ThrowingResolver(IAutomationOwnerResolver inner, string broken) : IAutomationOwnerResolver
    {
        public MutationPrincipal Resolve(string ownerId) =>
            ownerId == broken ? throw new InvalidOperationException("unreadable") : inner.Resolve(ownerId);
        public OwnerDescription Describe(string ownerId) =>
            ownerId == broken ? throw new InvalidOperationException("unreadable") : inner.Describe(ownerId);
    }

    /// <summary>Records, at each hold, what <paramref name="rowAbsent"/> says; then holds.</summary>
    private sealed class ProbingHold(IAutomationHold inner, Func<bool> rowAbsent) : IAutomationHold
    {
        public List<bool> RowAbsentAtHold { get; } = [];
        public Task<IReadOnlyList<HandoverItemResult>> HoldAllAsync(string ownerId, CancellationToken ct = default)
        {
            RowAbsentAtHold.Add(rowAbsent());
            return inner.HoldAllAsync(ownerId, ct);
        }
    }
}
