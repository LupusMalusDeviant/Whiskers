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
        GuardrailPolicy? policy = null)
    {
        var fixture = AutomationMutationRunnerTests.Build(policy: policy);
        fixture.Whitelist.Allowed.Add(Admin);
        fixture.Roles.Map[Admin] = AppRole.Admin;
        var host = new Host().Answering(Getent("93.184.215.14"), "__rc=0\n");
        var executor = MutationScopeProxy<IHostCommandExecutor>.Wrap(host, _guard);
        return (new EndpointCheckRunner(fixture.Runner, executor, _servers), host, fixture);
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

        // "Run now" without the UI's decision around it: the host command primitive refuses.
        await Assert.ThrowsAsync<MutationScopeViolationException>(() =>
            runner.RunInDecisionAsync(Definition(EndpointCheckKind.Tcp, "93.184.215.14", 443), CancellationToken.None));
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
            NullLogger<EndpointCheckMonitor>.Instance);

        fixture.Roles.Map[Admin] = AppRole.Operator;
        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        var suspended = RawStore().Get(check.Id)!;
        Assert.False(suspended.Enabled);
        Assert.Contains("Suspended", suspended.SuspendedReason);
        Assert.Contains(Admin, suspended.SuspendedReason);
        Assert.Empty(host.Calls);
        Assert.Empty(RawStore().Results(check.Id));
        Assert.Contains(notes.Events, e => e.EventType == NotificationEventTypes.AutomationSuspended && e.ServerId == "web");

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
            NullLogger<EndpointCheckMonitor>.Instance);

        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        var result = Assert.Single(RawStore().Results(check.Id));
        Assert.Equal(EndpointCheckOutcome.Passed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.OperationId));
        Assert.All(host.Calls, c => Assert.Equal(result.OperationId, c.OperationId));
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
