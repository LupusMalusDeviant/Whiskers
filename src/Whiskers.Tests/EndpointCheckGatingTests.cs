using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using static Whiskers.Tests.EndpointCheckTestDoubles;

namespace Whiskers.Tests;

/// <summary>
/// R1-57 (contract 0004, Gateway row): saving and deleting a check are gated, audited writes (R1-11, R1-22) — through the
/// real UI runner and Mutation Gateway against the real store behind the scope guard. Viewer, Operator and a demoted
/// Admin are refused, the kill switch refuses, an audit failure refuses before anything is written. A run is one
/// audited <c>endpointcheck.run</c> event with its own operation id, every host command inside that decision; an owner
/// who lost Admin suspends the check instead of running it; a Kubernetes server without SSH is refused on save.
/// </summary>
public sealed class EndpointCheckGatingTests : IDisposable
{
    private readonly Db _db = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);
    private readonly FakeServerConfig _servers = new(SshServer(),
        new Whiskers.Models.ServerConfig { Id = "k8s", Name = "k8s", ConnectionType = ConnectionType.Kubernetes },
        new Whiskers.Models.ServerConfig { Id = "k8s-ssh", Name = "k8s-ssh", ConnectionType = ConnectionType.Kubernetes, SshHost = "198.51.100.9" });
    private readonly MutationScopeMonitor _guard = new(NullLogger<MutationScopeMonitor>.Instance);

    public void Dispose() => _db.Dispose();

    private EndpointCheckStore RawStore() => new(_db.Scopes, _servers);

    /// <summary>The store as the app resolves it: behind the scope guard, so a write outside a decision is refused.</summary>
    private IEndpointCheckStore Store() => MutationScopeProxy<IEndpointCheckStore>.Wrap(RawStore(), _guard);

    private UiMutationRunner Ui(GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), _user);
    }

    private static GuardrailPolicy KillSwitch()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        return policy;
    }

    // As the /checks page runs them.
    private static Task<EndpointCheckDefinition> Save(IUiMutationRunner ui, IEndpointCheckStore store, EndpointCheckDraft draft) =>
        ui.RunAsync("endpointcheck.save", new { serverId = draft.ServerId, checkId = draft.Id },
            ct => store.SaveAsync(draft, AdminId, ct));

    private static Task Delete(IUiMutationRunner ui, IEndpointCheckStore store, EndpointCheckDefinition check) =>
        ui.RunAsync("endpointcheck.delete", new { serverId = check.ServerId, checkId = check.Id },
            ct => store.DeleteAsync(check.Id, ct));

    private static EndpointCheckDraft Tcp(string serverId = "web") =>
        Draft(EndpointCheckKind.Tcp, "db.lan", 5432, serverId: serverId,
            exceptions: [new OutboundExceptionEntry { Host = "db.lan", Port = 5432, Addresses = ["10.0.0.5"] }]);

    // ------------------------------------------------------------------------------------------------ save / delete

    [Fact]
    public async Task An_admin_save_is_audited_first_then_written_with_the_admin_as_owner()
    {
        var store = Store();
        var intentFirst = false;

        var saved = await Ui().RunAsync("endpointcheck.save", new { serverId = "web", checkId = (string?)null }, async ct =>
        {
            intentFirst = _audit.Events.Contains("begin:endpointcheck.save:") && store.List().Count == 0;
            return await store.SaveAsync(Tcp(), AdminId, ct);
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", _audit.Events);
        var stored = Assert.Single(store.List());
        Assert.Equal(saved.Id, stored.Id);
        Assert.Equal(AdminId, stored.OwnerId);
        Assert.Equal("10.0.0.5", Assert.Single(Assert.Single(stored.Exceptions).Addresses));
    }

    [Fact]
    public async Task A_write_outside_a_decision_is_refused_by_the_scope_guard()
    {
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => Store().SaveAsync(Tcp(), AdminId, default));
        Assert.Empty(RawStore().List());
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_cannot_save_and_nothing_is_written(AppRole role)
    {
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Ui(), Store(), Tcp()));

        Assert.Empty(RawStore().List());
        Assert.Contains("denied:Denied", _audit.Events);
        Assert.DoesNotContain(_audit.Events, e => e.StartsWith("begin:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_cannot_delete(AppRole role)
    {
        var store = Store();
        var check = await Save(Ui(), store, Tcp());
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Delete(Ui(), store, check));

        Assert.NotNull(RawStore().Get(check.Id));
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_can_neither_save_nor_delete()
    {
        var store = Store();
        var ui = Ui();
        var check = await Save(ui, store, Tcp());

        _user.Role = AppRole.Operator;
        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            Save(ui, store, Tcp() with { Id = check.Id, Name = "renamed", Exceptions = [] }));
        await Assert.ThrowsAsync<MutationRefusedException>(() => Delete(ui, store, check));

        var stored = RawStore().Get(check.Id)!;
        Assert.Equal("check", stored.Name);
        Assert.Single(stored.Exceptions);
    }

    [Fact]
    public async Task The_kill_switch_refuses_save_and_delete()
    {
        var store = Store();
        var check = await Save(Ui(), store, Tcp());

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Ui(KillSwitch()), store, Tcp()));
        await Assert.ThrowsAsync<MutationRefusedException>(() => Delete(Ui(KillSwitch()), store, check));

        Assert.Equal(check.Id, Assert.Single(RawStore().List()).Id);
    }

    [Fact]
    public async Task An_audit_failure_refuses_save_and_delete_before_anything_is_written()
    {
        var store = Store();
        _audit.FailNextBegin = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Save(Ui(), store, Tcp()));
        Assert.Empty(RawStore().List());

        var check = await Save(Ui(), store, Tcp());
        _audit.FailNextBegin = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Delete(Ui(), store, check));
        Assert.NotNull(RawStore().Get(check.Id));
    }

    [Fact]
    public async Task A_kubernetes_server_without_ssh_is_refused_on_save()
    {
        var store = Store();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Save(Ui(), store, Tcp("k8s")));

        Assert.Contains("no shell transport", refused.Message);
        Assert.Empty(RawStore().List());
        Assert.Contains("complete:Failed", _audit.Events);           // on record, as a failed write

        // With an SSH host the same cluster can run checks.
        await Save(Ui(), store, Tcp("k8s-ssh"));
        Assert.Single(RawStore().List());
    }

    // ------------------------------------------------------------------------------------------------ run

    private (EndpointCheckRunner Runner, Host Host, AutomationMutationRunnerTests.Fixture Fixture) RunFixture(
        GuardrailPolicy? policy = null, IServiceScopeFactory? trail = null)
    {
        var fixture = AutomationMutationRunnerTests.Build(policy: policy, trail: trail);
        fixture.Whitelist.Allowed.Add(Admin);
        fixture.Roles.Map[Admin] = AppRole.Admin;
        var host = new Host().Answering(Getent("93.184.215.14"), "__rc=0\n");
        var executor = MutationScopeProxy<IHostCommandExecutor>.Wrap(host, _guard);
        return (new EndpointCheckRunner(fixture.Runner, executor, _servers, RawStore()), host, fixture);
    }

    [Fact]
    public async Task A_run_is_one_audited_event_with_its_own_operation_id_and_every_command_runs_inside_it()
    {
        var (runner, host, fixture) = RunFixture();

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.True(run.Outcome == EndpointCheckOutcome.Passed, run.Detail);
        Assert.False(string.IsNullOrEmpty(run.OperationId));
        Assert.Equal(["begin:endpointcheck.run:", "complete:Executed"], fixture.Audit.Events);
        Assert.Equal(2, host.Calls.Count);                                   // resolve + one probe
        Assert.All(host.Calls, c =>
        {
            Assert.Equal("endpointcheck.run", c.Capability);
            Assert.Equal(run.OperationId, c.OperationId);
        });

        // As the check's owner, never as a free-standing service.
        var principal = Assert.Single(fixture.Audit.Principals);
        Assert.Equal(MutationPrincipalKind.Automation, principal.Kind);
        Assert.Equal(AdminId, principal.OnBehalfOf?.Id);

        // The next run is a new event with a new operation id.
        var next = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);
        Assert.NotEqual(run.OperationId, next.OperationId);
        Assert.Equal(4, fixture.Audit.Events.Count);
    }

    [Fact]
    public async Task A_failing_target_is_still_one_executed_event_its_outcome_is_the_runs()
    {
        var (runner, host, fixture) = RunFixture();
        host.Answering(Getent("93.184.215.14"), "__rc=1\n");

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.NotNull(run.OperationId);
        Assert.Single(fixture.Audit.Events, e => e.StartsWith("begin:endpointcheck.run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_definition_that_no_longer_validates_is_one_refused_event_and_runs_nothing()
    {
        // Saved while the server had SSH; it lost its shell transport since.
        var (runner, host, fixture) = RunFixture();

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: "k8s"), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Equal(["denied:Denied"], fixture.Audit.Events);
        Assert.NotNull(run.OperationId);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task The_kill_switch_refuses_a_run_and_nothing_reaches_the_host()
    {
        var (runner, host, _) = RunFixture(KillSwitch());

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task An_owner_without_admin_is_refused_by_the_gateway_and_nothing_reaches_the_host()
    {
        var (runner, host, fixture) = RunFixture();
        fixture.Roles.Map[Admin] = AppRole.Operator;

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Contains("denied:Denied", fixture.Audit.Events);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_probe_outside_a_decision_is_refused_by_the_scope_guard()
    {
        var (runner, host, _) = RunFixture();
        var check = await Save(Ui(), Store(), Draft(EndpointCheckKind.Tcp, "93.184.215.14", 443));

        // "Run now" without the UI's decision around it: the host command primitive refuses.
        await Assert.ThrowsAsync<MutationScopeViolationException>(() =>
            runner.RunInDecisionAsync(check.Id, CancellationToken.None));
        Assert.Empty(host.Calls);
    }

    // ------------------------------------------------------------------------------------------------ review round 1

    private static EndpointCheckDraft Internal(string? id = null, bool withException = true) =>
        Draft(EndpointCheckKind.Tcp, "10.0.0.5", 5432, id: id,
            exceptions: withException ? [new OutboundExceptionEntry { Host = "10.0.0.5", Port = 5432 }] : []);

    [Fact]
    public async Task Run_now_after_another_admin_removed_the_exception_is_refused_and_reaches_no_host()
    {
        // Review M1: the page holds the definition it loaded; another Admin removes the exception meanwhile.
        var (runner, host, _) = RunFixture();
        var store = Store();
        var ui = Ui();
        var check = await Save(ui, store, Internal());
        var loaded = RawStore().Get(check.Id)!;
        await Save(ui, store, Internal(check.Id, withException: false));

        var run = await ui.RunAsync("endpointcheck.run", new { serverId = loaded.ServerId, checkId = loaded.Id },
            ct => runner.RunInDecisionAsync(loaded.Id, ct));

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Run_now_of_a_deleted_or_switched_off_check_is_refused_and_reaches_no_host()
    {
        var (runner, host, _) = RunFixture();
        var store = Store();
        var ui = Ui();
        var deleted = await Save(ui, store, Draft(EndpointCheckKind.Tcp, "93.184.215.14", 443));
        await Delete(ui, store, deleted);
        var off = await Save(ui, store, Draft(EndpointCheckKind.Tcp, "93.184.215.14", 444) with { Enabled = false });

        foreach (var check in new[] { deleted, off })
        {
            var run = await ui.RunAsync("endpointcheck.run", new { serverId = check.ServerId, checkId = check.Id },
                ct => runner.RunInDecisionAsync(check.Id, ct));
            Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        }
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Run_now_of_a_switched_off_or_suspended_check_leaves_no_result_but_a_refusal_that_no_longer_validates_does()
    {
        // Review n1: the no-op answer for a check that is off (suspended included) is not a run and is not recorded —
        // also when a page records whatever it got back, as it did before the fix.
        var (runner, host, _) = RunFixture();
        var store = Store();
        var ui = Ui();
        var off = await Save(ui, store, Draft(EndpointCheckKind.Tcp, "93.184.215.14", 443) with { Enabled = false });
        var suspended = await Save(ui, store, Draft(EndpointCheckKind.Tcp, "93.184.215.14", 444));
        Assert.True(await RawStore().SuspendAsync(suspended.Id, AdminId, "Suspended: the owner lost the Admin role."));

        foreach (var check in new[] { off, suspended })
        {
            var run = await ui.RunAsync("endpointcheck.run", new { serverId = check.ServerId, checkId = check.Id },
                ct => runner.RunInDecisionAsync(check.Id, ct));
            await RawStore().RecordAsync(run);

            Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
            Assert.True(run.NothingRan);
            Assert.Empty(RawStore().Results(check.Id));
            Assert.Null(RawStore().Get(check.Id)!.LastOutcome);
        }
        Assert.Empty(host.Calls);

        // A check that is on but no longer passes is a real refusal: recorded.
        var invalid = await Save(ui, store, Internal());
        await Save(ui, store, Internal(invalid.Id, withException: false));
        var refused = await ui.RunAsync("endpointcheck.run", new { serverId = invalid.ServerId, checkId = invalid.Id },
            ct => runner.RunInDecisionAsync(invalid.Id, ct));
        await RawStore().RecordAsync(refused);
        Assert.False(refused.NothingRan);
        Assert.Equal(EndpointCheckOutcome.Refused, Assert.Single(RawStore().Results(invalid.Id)).Outcome);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Run_now_runs_the_stored_check_inside_the_ui_decision_and_records_its_operation_id()
    {
        // Review T1/m2: one audited UI event; every probe inside it; the result carries its operation id.
        var (runner, host, _) = RunFixture();
        var store = Store();
        var ui = Ui();
        var check = await Save(ui, store, Draft(EndpointCheckKind.Tcp, "example.com", 443));
        _audit.Events.Clear();

        var run = await ui.RunAsync("endpointcheck.run", new { serverId = check.ServerId, checkId = check.Id },
            ct => runner.RunInDecisionAsync(check.Id, ct));
        await RawStore().RecordAsync(run);

        Assert.True(run.Outcome == EndpointCheckOutcome.Passed, run.Detail);
        Assert.Equal(["begin:endpointcheck.run:", "complete:Executed"], _audit.Events);
        Assert.False(string.IsNullOrEmpty(run.OperationId));
        Assert.Equal(2, host.Calls.Count);
        Assert.All(host.Calls, c =>
        {
            Assert.Equal("endpointcheck.run", c.Capability);
            Assert.Equal(run.OperationId, c.OperationId);
        });
        Assert.Equal(run.OperationId, Assert.Single(RawStore().Results(check.Id)).OperationId);
        // Review n2: recorded as the last outcome, but the monitor's schedule is not moved.
        Assert.True(run.OnDemand);
        Assert.Equal(EndpointCheckOutcome.Passed, RawStore().Get(check.Id)!.LastOutcome);
        Assert.Null(RawStore().Get(check.Id)!.LastRunUtc);
    }

    [Fact]
    public async Task A_check_without_an_owner_is_refused_and_runs_nothing()
    {
        // Review m5: with the owner grace period on (RequireOwner = false) an ownerless item would get the capability's
        // own level — Admin, for a root-equivalent command.
        var (runner, host, fixture) = RunFixture();

        var run = await runner.RunAsync(Definition(EndpointCheckKind.Tcp, "93.184.215.14", 443, ownerId: ""), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
        Assert.Equal(["denied:Denied"], fixture.Audit.Events);
        Assert.Equal(McpPermissionLevels.Read, Assert.Single(fixture.Audit.Principals).PermissionLevel);
    }

    [Fact]
    public async Task The_monitor_suspends_a_check_without_an_owner()
    {
        var (runner, host, fixture) = RunFixture();
        var check = await Save(Ui(), Store(), Draft(EndpointCheckKind.Tcp, "93.184.215.14", 443));
        using (var scope = _db.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>().EndpointChecks
                .Where(c => c.Id == check.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, ""));
        var monitor = new EndpointCheckMonitor(RawStore(), runner, fixture.Runner, _servers, new FakeNotifications(),
            NullLogger<EndpointCheckMonitor>.Instance, new EndpointCheckSlots());

        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        var stored = RawStore().Get(check.Id)!;
        Assert.False(stored.Enabled);
        Assert.Contains("no owner", stored.SuspendedReason);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task An_owner_who_lost_admin_suspends_the_check_instead_of_running_it()
    {
        var (runner, host, fixture) = RunFixture();
        var notes = new FakeNotifications();
        var store = Store();
        var check = await Save(Ui(), store, Draft(EndpointCheckKind.Tcp, "example.com", 443));
        var monitor = new EndpointCheckMonitor(RawStore(), runner, fixture.Runner, _servers, notes,
            NullLogger<EndpointCheckMonitor>.Instance, new EndpointCheckSlots());

        fixture.Roles.Map[Admin] = AppRole.Operator;
        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        var suspended = RawStore().Get(check.Id)!;
        Assert.False(suspended.Enabled);
        Assert.Contains("Suspended", suspended.SuspendedReason);
        Assert.Contains(Admin, suspended.SuspendedReason);
        Assert.Empty(host.Calls);
        Assert.Empty(RawStore().Results(check.Id));
        var note = Assert.Single(notes.Events, e => e.EventType == NotificationEventTypes.AutomationSuspended && e.ServerId == "web");
        // Review round 3 N1: notifications reach every role; the former owner stays in the check's reason (Admin-only).
        Assert.DoesNotContain("@", note.ImageInfo);
        Assert.DoesNotContain("user:", note.ImageInfo);

        // It stays off on the next cycle; an Admin saving it again takes it over.
        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);
        Assert.Empty(host.Calls);
        var taken = await Save(Ui(), store, Draft(EndpointCheckKind.Tcp, "example.com", 443, id: check.Id));
        Assert.True(taken.Enabled);
        Assert.Null(taken.SuspendedReason);
    }

    [Fact]
    public async Task An_owner_who_keeps_admin_runs_and_the_run_is_recorded_with_its_operation_id()
    {
        var (runner, host, fixture) = RunFixture();
        var store = Store();
        var check = await Save(Ui(), store, Draft(EndpointCheckKind.Tcp, "example.com", 443));
        var monitor = new EndpointCheckMonitor(RawStore(), runner, fixture.Runner, _servers, new FakeNotifications(),
            NullLogger<EndpointCheckMonitor>.Instance, new EndpointCheckSlots());

        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        var result = Assert.Single(RawStore().Results(check.Id));
        Assert.Equal(EndpointCheckOutcome.Passed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.OperationId));
        Assert.All(host.Calls, c => Assert.Equal(result.OperationId, c.OperationId));
    }

    // ------------------------------------------------------------------------------------------------ INV-45

    /// <summary>INV-45, S-16: each due run is one complete <c>endpointcheck.run</c> event in the real <c>AuditLog</c> —
    /// executed (a failing target included: that is the run's answer), failed (the host command threw), refused by the
    /// kill switch, refused before the gateway (no owner, a definition that no longer validates), and refused by the
    /// monitor for an owner who lost Admin or a check without an owner (found by the path matrix: the monitor
    /// suspended those without an event).</summary>
    [Fact]
    [AuditPath("S-16", AuditTrail.Executed, AuditTrail.Denied, AuditTrail.Failed)]
    public async Task Each_due_run_is_one_complete_event_in_the_audit_log()
    {
        var trail = _db.Scopes;
        Task<List<AuditLogEntity>> RunAsync(EndpointCheckRunner runner, EndpointCheckDefinition check) =>
            AuditTrail.AttemptAsync(trail, () => runner.RunAsync(check, CancellationToken.None));
        var tcp = Definition(EndpointCheckKind.Tcp, "example.com", 443);

        var (runner, host, fixture) = RunFixture(trail: trail);
        var row = AuditTrail.One(await RunAsync(runner, tcp), AuditTrail.Executed, "endpointcheck.run", "Automation");
        Assert.Equal(AdminId, row.OnBehalfOfId);

        host.Answer = (_, _, _) => throw new InvalidOperationException("the SSH connection dropped");
        AuditTrail.One(await RunAsync(runner, tcp), AuditTrail.Failed, "endpointcheck.run", "Automation");

        var (killed, _, _) = RunFixture(KillSwitch(), trail);
        AuditTrail.One(await RunAsync(killed, tcp), AuditTrail.Denied, "endpointcheck.run", "Automation");

        AuditTrail.One(await RunAsync(runner, Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: "k8s")),
            AuditTrail.Denied, "endpointcheck.run", "Automation", decided: false);
        AuditTrail.One(await RunAsync(runner, Definition(EndpointCheckKind.Tcp, "example.com", 443, ownerId: "")),
            AuditTrail.Denied, "endpointcheck.run", "Automation", owned: false, decided: false);

        // The monitor: an owner who lost Admin, and a check without an owner, are refused, recorded and suspended.
        var (monitorRunner, monitorHost, monitorFixture) = RunFixture(trail: trail);
        var demoted = await Save(Ui(), Store(), Draft(EndpointCheckKind.Tcp, "example.com", 443));
        var monitor = new EndpointCheckMonitor(RawStore(), monitorRunner, monitorFixture.Runner, _servers, new FakeNotifications(),
            NullLogger<EndpointCheckMonitor>.Instance, new EndpointCheckSlots());
        monitorFixture.Roles.Map[Admin] = AppRole.Operator;
        var lost = await AuditTrail.AttemptAsync(trail, async () =>
        {
            await monitor.CycleAsync(CancellationToken.None);
            await SettleAsync(monitor);
        });
        AuditTrail.One(lost, AuditTrail.Denied, "endpointcheck.run", "Automation", decided: false);
        Assert.False(RawStore().Get(demoted.Id)!.Enabled);

        monitorFixture.Roles.Map[Admin] = AppRole.Admin;
        var unowned = await Save(Ui(), Store(), Draft(EndpointCheckKind.Tcp, "example.com", 444));
        using (var scope = _db.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>().EndpointChecks
                .Where(c => c.Id == unowned.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, ""));
        var none = await AuditTrail.AttemptAsync(trail, async () =>
        {
            await monitor.CycleAsync(CancellationToken.None);
            await SettleAsync(monitor);
        });
        AuditTrail.One(none, AuditTrail.Denied, "endpointcheck.run", "Automation", owned: false, decided: false);
        Assert.False(RawStore().Get(unowned.Id)!.Enabled);
        Assert.Empty(monitorHost.Calls);
    }

    /// <summary>The store with a suspension that cannot be saved.</summary>
    private sealed class UnsuspendableStore(IEndpointCheckStore inner) : IEndpointCheckStore
    {
        public IReadOnlyList<EndpointCheckDefinition> List(string? serverId = null) => inner.List(serverId);
        public EndpointCheckDefinition? Get(string id) => inner.Get(id);
        public Task<EndpointCheckDefinition> SaveAsync(EndpointCheckDraft draft, string editorId, CancellationToken ct) =>
            inner.SaveAsync(draft, editorId, ct);
        public Task DeleteAsync(string id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task<bool> SuspendAsync(string id, string expectedOwnerId, string reason, CancellationToken ct = default) =>
            throw new IOException("the database is locked");
        public Task RecordAsync(EndpointCheckRun run, CancellationToken ct = default) => inner.RecordAsync(run, ct);
        public IReadOnlyList<EndpointCheckResultEntity> Results(string checkId, int take = 50) => inner.Results(checkId, take);
        public IReadOnlyDictionary<string, EndpointCheckResultEntity> LastResults() => inner.LastResults();
        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default) => inner.PruneAsync(cutoffUtc, ct);
    }

    /// <summary>INV-45 review (MINOR 4): a check whose owner lost the authority and whose suspension cannot be saved
    /// stays on — each refused run is still one event, but at the check's interval, not every 15-second tick.</summary>
    [Fact]
    public async Task A_refused_check_that_cannot_be_suspended_is_refused_once_per_interval_not_every_tick()
    {
        var (runner, host, fixture) = RunFixture(trail: _db.Scopes);
        await Save(Ui(), Store(), Draft(EndpointCheckKind.Tcp, "example.com", 443));
        var clock = new ManualTime();
        var monitor = new EndpointCheckMonitor(new UnsuspendableStore(RawStore()), runner, fixture.Runner, _servers,
            new FakeNotifications(), NullLogger<EndpointCheckMonitor>.Instance, new EndpointCheckSlots(), time: clock);
        fixture.Roles.Map[Admin] = AppRole.Operator;
        async Task<List<AuditLogEntity>> TickAsync() => await AuditTrail.AttemptAsync(_db.Scopes, async () =>
        {
            await monitor.CycleAsync(CancellationToken.None);
            await SettleAsync(monitor);
        });

        AuditTrail.One(await TickAsync(), AuditTrail.Denied, "endpointcheck.run", "Automation", decided: false);
        clock.Advance(EndpointCheckMonitor.Tick);
        Assert.Empty(await TickAsync());                   // the next tick: not due yet, no second event

        clock.Advance(TimeSpan.FromHours(1));              // past the interval: refused again, one event
        AuditTrail.One(await TickAsync(), AuditTrail.Denied, "endpointcheck.run", "Automation", decided: false);
        Assert.Empty(host.Calls);

        // Review NIT-1: saved since (an Admin who holds the role took it over) it is a new definition — due at once, not
        // an interval after the refusal; it runs, and the refusal no longer holds it back.
        fixture.Roles.Map[Admin] = AppRole.Admin;
        await RawStore().SaveAsync(RawStore().List().Single().ToDraft(), AdminId, CancellationToken.None);
        clock.Advance(EndpointCheckMonitor.Tick);
        Assert.Single(await TickAsync());
        Assert.NotEmpty(host.Calls);
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
