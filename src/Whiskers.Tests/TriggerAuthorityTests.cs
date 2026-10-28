using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using PresetStore = Whiskers.Tests.PolicyDecisionRecordTests.PresetStore;

namespace Whiskers.Tests;

/// <summary>PC-D6 / PC-D7 for AI triggers (R1-13, INV-04, INV-27): a trigger is read-only unless an Admin granted it
/// capabilities and targets; a granted write runs only while the owner of the grant holds the authority, re-checked
/// for every mutation; out-of-grant writes and every unattended Confirm are refused; <c>trigger:&lt;id&gt;</c> stays on
/// every audit row of the run.</summary>
public sealed class TriggerAuthorityTests : IDisposable
{
    private const string Admin = "user:admin@example.com";
    private static readonly Capability Restart = new CapabilityCatalog().Find("container.restart")!;
    private static readonly Capability Remove = new CapabilityCatalog().Find("container.remove")!;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whiskers-trigger-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;

    public TriggerAuthorityTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "m.db")}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- Read-only by default ---------------------------------------------

    [Fact]
    public async Task A_new_trigger_is_read_only()
    {
        var trigger = new AiTrigger();
        var owners = new Owners();

        var start = await new AiTriggerAuthority(new MemoryTriggers(trigger), owners).BeginRunAsync(trigger);

        Assert.False(trigger.HasWriteGrant);
        Assert.Equal((McpPermissionLevels.Read, (string?)null), (start.Level, start.Refusal));
    }

    [Fact]
    public async Task A_trigger_saved_before_R1_13_is_read_only_whatever_its_MaxLevel()
    {
        var path = Path.Combine(_dir, "ai-triggers.json");
        await File.WriteAllTextAsync(path, """
            { "Triggers": [ { "Id": "t1", "Name": "fix", "Enabled": true, "EventTypes": ["unhealthy"],
                              "GuardrailPreset": "Standard", "MaxLevel": "admin", "OwnerId": "user:admin@example.com" } ] }
            """);
        var store = new AiTriggerStore(path);
        await store.InitializeAsync();

        var trigger = Assert.Single(store.Triggers);
        Assert.False(trigger.HasWriteGrant);
        Assert.Equal(McpPermissionLevels.Read, (await new AiTriggerAuthority(store, new Owners()).BeginRunAsync(trigger)).Level);
        var outcome = await Gateway(store, new Owners()).ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    // ---- Ownership is the store's ------------------------------------------

    [Fact]
    public async Task Granting_writes_makes_the_admin_the_owner_and_a_sent_owner_is_ignored()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        var forged = Granted();
        forged.OwnerId = "user:someone-else@example.com";
        forged.SuspendedReason = null;

        await store.SaveAsync([forged], Editor("admin@example.com"));
        Assert.Equal(Admin, Assert.Single(store.Triggers).OwnerId);

        // Another admin saves without touching the grant: the owner stays.
        await store.SaveAsync([Copy(store.Triggers[0], t => t.Prompt = "changed")], Editor("other@example.com"));
        Assert.Equal(Admin, store.Triggers[0].OwnerId);

        // Changing the grant makes the editor the owner; removing it leaves nothing to own.
        await store.SaveAsync([Copy(store.Triggers[0], t => t.WriteTargets = ["web", "api"])], Editor("other@example.com"));
        Assert.Equal("user:other@example.com", store.Triggers[0].OwnerId);
        await store.SaveAsync([Copy(store.Triggers[0], t => t.WriteCapabilities = [])], Editor("other@example.com"));
        Assert.Null(store.Triggers[0].OwnerId);
        Assert.False(store.Triggers[0].HasWriteGrant);
    }

    [Fact]
    public async Task Switching_a_suspended_trigger_on_again_is_a_takeover()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await store.SaveAsync([Granted()], Editor("admin@example.com"));
        Assert.True(await store.SuspendAsync("t1", Admin, "its owner has no authority left"));
        Assert.False(store.Triggers[0].Enabled);
        Assert.Equal("its owner has no authority left", store.Triggers[0].SuspendedReason);

        // A save that leaves it off keeps the suspension (and the owner).
        await store.SaveAsync([Copy(store.Triggers[0], t => t.SuspendedReason = null)], Editor("other@example.com"));
        Assert.NotNull(store.Triggers[0].SuspendedReason);
        Assert.Equal(Admin, store.Triggers[0].OwnerId);

        await store.SaveAsync([Copy(store.Triggers[0], t => t.Enabled = true)], Editor("other@example.com"));
        Assert.Null(store.Triggers[0].SuspendedReason);
        Assert.Equal("user:other@example.com", store.Triggers[0].OwnerId);
    }

    [Fact]
    public async Task A_suspension_for_an_owner_who_was_replaced_meanwhile_does_nothing()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await store.SaveAsync([Granted()], Editor("other@example.com"));

        Assert.False(await store.SuspendAsync("t1", Admin, "lost"));
        Assert.True(store.Triggers[0].Enabled);
    }

    // ---- The gateway enforces the grant ------------------------------------

    [Fact]
    public async Task A_granted_capability_on_a_granted_target_runs()
    {
        var outcome = await Gateway(new MemoryTriggers(Granted()), new Owners())
            .ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    [Theory]
    [InlineData("container.remove", "web")]      // a capability that was not granted
    [InlineData("container.restart", "db")]      // a target that was not granted
    [InlineData("container.restart", "web2")]    // a pattern is matched whole, not as a prefix
    public async Task A_write_outside_the_grant_is_refused(string capability, string target)
    {
        var ran = false;
        var outcome = await Gateway(new MemoryTriggers(Granted()), new Owners())
            .ExecuteAsync(AgentCall(new CapabilityCatalog().Find(capability)!, target), (_, _) => { ran = true; return Task.FromResult(1); });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    [Fact]
    public async Task A_switched_off_or_deleted_trigger_cannot_write_even_mid_run()
    {
        var triggers = new MemoryTriggers(Granted());
        var gateway = Gateway(triggers, new Owners());

        triggers.Items[0].Enabled = false;
        var off = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        triggers.Items.Clear();
        var gone = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, off.Status);
        Assert.Equal(MutationStatus.Denied, gone.Status);
    }

    [Fact]
    public async Task Without_a_trigger_authority_every_trigger_write_is_refused()
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance);

        var outcome = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    // ---- The owner's authority, per mutation -------------------------------

    [Fact]
    public async Task The_owners_authority_is_rechecked_for_every_mutation_and_its_loss_suspends_the_trigger()
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        var notify = new Notify();
        var services = new ServiceCollection().AddSingleton<INotificationService>(notify).BuildServiceProvider();
        var gateway = Gateway(triggers, owners, services);

        var first = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        owners.Levels[Admin] = McpPermissionLevels.Read;                 // the admin was demoted during the run
        var second = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, first.Status);
        Assert.Equal(MutationStatus.Denied, second.Status);
        Assert.Contains("no longer holds 'write'", second.Reason);
        Assert.False(triggers.Items[0].Enabled);
        Assert.NotNull(triggers.Items[0].SuspendedReason);
        var sent = Assert.Single(notify.Events);
        Assert.Equal("automation_suspended", sent.EventType);
    }

    [Theory]
    [InlineData(OwnerState.NotAllowedToSignIn)]
    [InlineData(OwnerState.AccountDeleted)]
    public async Task An_owner_who_can_no_longer_sign_in_suspends_the_trigger_before_its_run_starts(OwnerState state)
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        owners.States[Admin] = state;

        var start = await new AiTriggerAuthority(triggers, owners).BeginRunAsync(triggers.Items[0]);

        Assert.NotNull(start.Refusal);
        Assert.False(triggers.Items[0].Enabled);
    }

    [Fact]
    public async Task A_granted_run_starts_with_its_owners_current_level_never_more()
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        owners.Levels[Admin] = McpPermissionLevels.Write;

        var start = await new AiTriggerAuthority(triggers, owners).BeginRunAsync(triggers.Items[0]);

        Assert.Equal((McpPermissionLevels.Write, (string?)null), (start.Level, start.Refusal));
    }

    // ---- Unattended Confirm ------------------------------------------------

    [Fact]
    public async Task Every_unattended_Confirm_is_refused_and_never_reaches_the_approval_gate()
    {
        var approvals = new CountingApprovals();
        var gateway = new MutationGateway(new CapabilityCatalog(), new ConfirmAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance, approvals: approvals,
            triggers: new AiTriggerAuthority(new MemoryTriggers(Granted()), new Owners()));

        var executed = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        var decided = await gateway.DecideAsync(AgentCall(Restart, "web"));
        // A mutation nested in the trigger's one (here: made by the person-owned task it ran) is unattended as well.
        MutationOutcome<int>? nested = null;
        var outer = await new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance, approvals: approvals,
            triggers: new AiTriggerAuthority(new MemoryTriggers(Granted()), new Owners()))
            .ExecuteAsync(AgentCall(Restart, "web"), async (_, _) =>
            {
                nested = await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Ui), (_, _) => Task.FromResult(1));
                return 1;
            });

        Assert.Equal(MutationStatus.Denied, executed.Status);
        Assert.Equal(MutationReasonCodes.UnattendedConfirm, executed.ReasonCode);
        Assert.Equal(MutationVerdict.Deny, decided!.Verdict);
        Assert.Equal(MutationStatus.Executed, outer.Status);
        Assert.Equal(MutationStatus.Denied, nested!.Status);
        Assert.Equal(0, approvals.Checks);
    }

    // ---- trigger:<id> on every audit row ------------------------------------

    [Fact]
    public async Task Every_audit_row_of_a_trigger_run_names_the_trigger_nested_mutations_included()
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(),
            new AuditLogMutationSink(_sp.GetRequiredService<IServiceScopeFactory>()), [], NullLogger<MutationGateway>.Instance,
            triggers: new AiTriggerAuthority(new MemoryTriggers(Granted()), new Owners()));

        await gateway.ExecuteAsync(AgentCall(Restart, "web"), async (_, _) =>
        {
            await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Automation), (_, _) => Task.FromResult(1));
            return 1;
        });
        await gateway.ExecuteAsync(AgentCall(Remove, "web"), (_, _) => Task.FromResult(1));    // refused

        using var scope = _sp.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            using var details = JsonDocument.Parse(row.Details!);
            Assert.Equal("trigger:t1", details.RootElement.GetProperty("trigger").GetString());
        });
    }

    // ---- Helpers -----------------------------------------------------------

    private static AiTrigger Granted() => new()
    {
        Id = "t1", Name = "restart web", EventTypes = ["unhealthy"], GuardrailPreset = "Standard",
        WriteCapabilities = [Restart.Id], WriteTargets = ["web", "shop-*"], OwnerId = Admin,
    };

    private static AiTrigger Copy(AiTrigger t, Action<AiTrigger> change)
    {
        var copy = AiTriggerStore.Normalized(t);
        change(copy);
        return copy;
    }

    private static AgentPrincipal Editor(string email) =>
        new(AgentPrincipalKind.WebUser, email, McpPermissionLevels.Admin, null, UserEmail: email);

    /// <summary>The agent of trigger t1's run asking for <paramref name="capability"/> on container <paramref name="name"/>.</summary>
    internal static MutationRequest AgentCall(Capability capability, string name)
    {
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart web'", McpPermissionLevels.Admin,
            null, TriggerId: "t1");
        return new MutationRequest(capability.Id, MutationPrincipals.Agent(trigger, "run-1"), MutationSurface.Agent,
            new MutationTarget("container", "id-" + name, name, "srv1"),
            new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(name) }, SessionId: "run-1");
    }

    private static MutationRequest PersonsCall(Capability capability, string name, MutationSurface surface) => new(capability.Id,
        new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com", McpPermissionLevels.Admin),
        surface, new MutationTarget("container", "id-" + name, name, "srv1"),
        new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(name) });

    private static MutationGateway Gateway(IAiTriggerStore triggers, IAutomationOwnerResolver owners,
        IServiceProvider? sp = null) =>
        new(new CapabilityCatalog(), new AllowAll(), new Audit(), [], NullLogger<MutationGateway>.Instance,
            triggers: new AiTriggerAuthority(triggers, owners, sp));

    internal sealed class MemoryTriggers(params AiTrigger[] triggers) : IAiTriggerStore
    {
        public List<AiTrigger> Items { get; } = triggers.ToList();
        public IReadOnlyList<AiTrigger> Triggers => Items;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor) => throw new NotSupportedException();

        public Task<bool> SuspendAsync(string triggerId, string expectedOwnerId, string reason)
        {
            var t = Items.FirstOrDefault(x => x.Id == triggerId && x.OwnerId == expectedOwnerId && x.Enabled);
            if (t is null) return Task.FromResult(false);
            t.Enabled = false;
            t.SuspendedReason = reason;
            return Task.FromResult(true);
        }
    }

    internal sealed class Owners : IAutomationOwnerResolver
    {
        public Dictionary<string, string> Levels { get; } = new() { [Admin] = McpPermissionLevels.Admin };
        public Dictionary<string, OwnerState> States { get; } = new();

        public MutationPrincipal Resolve(string ownerId) => new(MutationPrincipalKind.Human, ownerId, ownerId,
            States.GetValueOrDefault(ownerId, OwnerState.Active) == OwnerState.Active
                ? Levels.GetValueOrDefault(ownerId, McpPermissionLevels.Read)
                : McpPermissionLevels.Read);

        public OwnerDescription Describe(string ownerId) => new(ownerId, States.GetValueOrDefault(ownerId, OwnerState.Active));
    }

    private sealed class AllowAll : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(MutationVerdict.Allow, "ok", []);
    }

    private sealed class ConfirmAll : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(MutationVerdict.Confirm, "needs a human", ["confirm-writes"]);
    }

    private sealed class CountingApprovals : IApprovalGate
    {
        public int Checks;

        public Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref Checks);
            throw new InvalidOperationException("an unattended Confirm must never reach the approval gate");
        }

        public bool TryConsume(string approvalId) => false;
    }

    private sealed class Audit : IMutationAuditSink
    {
        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct) => Task.FromResult(1L);

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notify : INotificationService
    {
        public List<NotificationEvent> Events { get; } = [];
        public Task SendAsync(NotificationEvent evt) { lock (Events) Events.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }
}
