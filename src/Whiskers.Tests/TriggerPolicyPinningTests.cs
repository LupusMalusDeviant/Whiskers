using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Providers;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Scheduler;
using PresetStore = Whiskers.Tests.PolicyDecisionRecordTests.PresetStore;

namespace Whiskers.Tests;

/// <summary>INV-23 (R1-12): an AI trigger's run is governed by the trigger's preset, fixed when the run starts —
/// switching or editing the active preset does not reach it, only the global kill switch does — and a preset that no
/// longer exists stops the run instead of falling back to another preset.</summary>
public class TriggerPolicyPinningTests
{
    private static readonly Capability Start = new CapabilityCatalog().Find("container.start")!;

    /// <summary>Lets writes run without confirmation.</summary>
    private static GuardrailPolicy Open() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Write,
        RequireConfirmationForWrites = false,
    };

    /// <summary>Denies starting containers.</summary>
    private static GuardrailPolicy NoStart()
    {
        var policy = Open();
        policy.ToolDenyList.Add(Start.McpTool!);
        return policy;
    }

    private static MutationRequest TriggerCall(PinnedPolicy? pinned)
    {
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart'", McpPermissionLevels.Write,
            null, TriggerId: "t1");
        return new MutationRequest(Start.Id, MutationPrincipals.Agent(trigger, "run-1"), MutationSurface.Agent,
            new MutationTarget("container", "c1", "web", "srv1"), new Dictionary<string, JsonElement>
            {
                ["containerId"] = JsonSerializer.SerializeToElement("web"),
                ["serverId"] = JsonSerializer.SerializeToElement("srv1"),
            }, SessionId: "run-1") { PinnedPolicy = pinned };
    }

    private static PresetStore Store(string active = "Strict") => new(active,
        new GuardrailPreset { Name = "Strict", Policy = NoStart() },
        new GuardrailPreset { Name = "Repair", Policy = Open() });

    [Fact]
    public void The_pinned_preset_governs_the_call_not_the_active_one()
    {
        var store = Store(active: "Strict");
        var policy = AgentToolTestHelpers.Policy(store);
        var pinned = PinnedPolicy.Of("Repair", Open());

        var unpinned = policy.Evaluate(TriggerCall(null), Start);
        var decided = policy.Evaluate(TriggerCall(pinned), Start);

        Assert.Equal(MutationVerdict.Deny, unpinned.Verdict);
        Assert.Equal(MutationVerdict.Allow, decided.Verdict);
        Assert.Equal("Repair", decided.Policy!.Preset);
        Assert.Equal(PolicySnapshot.Of("Repair", Open()).VersionHash, decided.Policy.VersionHash);
    }

    [Fact]
    public void Switching_or_editing_presets_during_the_run_does_not_reach_it()
    {
        var store = Store(active: "Repair");
        var policy = AgentToolTestHelpers.Policy(store);
        var pinned = PinnedPolicy.Of("Strict", store.Config.Presets.Single(p => p.Name == "Strict").Policy);

        // Mid-run the operator relaxes the pinned preset and switches the active one: the run keeps what it started with.
        store.Presets.Single(p => p.Name == "Strict").Policy = Open();
        store.ActiveName = "Strict";

        Assert.Equal(MutationVerdict.Deny, policy.Evaluate(TriggerCall(pinned), Start).Verdict);
        Assert.Equal(MutationVerdict.Allow, policy.Evaluate(TriggerCall(null), Start).Verdict);
    }

    [Fact]
    public void The_global_kill_switch_still_stops_a_pinned_run()
    {
        var store = Store(active: "Strict");
        var policy = AgentToolTestHelpers.Policy(store);
        var pinned = PinnedPolicy.Of("Repair", Open());
        store.Presets.Single(p => p.Name == "Strict").Policy.ReadOnlyMode = true;   // kill switch on

        var decision = policy.Evaluate(TriggerCall(pinned), Start);

        Assert.Equal(MutationVerdict.Deny, decision.Verdict);
        Assert.Equal("Repair", decision.Policy!.Preset);
        var effective = EffectivePolicy.Of(pinned, store);
        Assert.True(effective.Policy.ReadOnlyMode);
        Assert.False(pinned.Policy.ReadOnlyMode);   // the pin itself is unchanged
    }

    [Fact]
    public void A_pinned_preset_cannot_switch_the_kill_switch_off_and_a_fail_closed_store_refuses_it()
    {
        var store = Store(active: "Repair");
        var killed = Open();
        killed.ReadOnlyMode = true;
        Assert.True(EffectivePolicy.Of(PinnedPolicy.Of("Killed", killed), store).Policy.ReadOnlyMode);

        store.Error = "guardrails.json is broken";
        var (preset, policy) = EffectivePolicy.Of(PinnedPolicy.Of("Repair", Open()), store);
        Assert.Equal(PolicySnapshot.FailClosedPreset, preset);
        Assert.True(policy.ReadOnlyMode);
        Assert.Equal(MutationVerdict.Deny,
            AgentToolTestHelpers.Policy(store).Evaluate(TriggerCall(PinnedPolicy.Of("Repair", Open())), Start).Verdict);
    }

    [Fact]
    public async Task A_trigger_run_starts_pinned_to_its_preset()
    {
        var store = Store(active: "Strict");
        var (dispatcher, agents, audit) = Dispatcher(store, preset: "Repair");

        await dispatcher.OnEventAsync(Event());
        var context = await agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Repair", context.Pinned!.Preset);
        Assert.False(context.Pinned.Policy.RequireConfirmationForWrites);
        Assert.Equal(AgentOrigin.Trigger, context.Origin);
        Assert.Empty(audit.Refusals);
    }

    [Fact]
    public async Task A_run_starts_at_the_level_its_authority_returns()
    {
        var (dispatcher, agents, _) = Dispatcher(Store(active: "Strict"), preset: "Repair",
            start: new TriggerRunAuthority(McpPermissionLevels.Write, null));

        await dispatcher.OnEventAsync(Event());
        var context = await agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(McpPermissionLevels.Write, context.Principal.PermissionLevel);
        Assert.Equal("t1", context.Principal.TriggerId);
    }

    [Theory]
    [InlineData(true)]     // the authority refuses (owner lost authority, no owner)
    [InlineData(false)]    // no authority registered at all
    public async Task A_run_its_authority_refuses_or_cannot_check_does_not_start(bool withAuthority)
    {
        var (dispatcher, agents, audit) = Dispatcher(Store(active: "Strict"), preset: "Repair",
            start: new TriggerRunAuthority(McpPermissionLevels.Read, "its owner has no authority left"),
            withAuthority: withAuthority);

        await dispatcher.OnEventAsync(Event());
        var refusal = await audit.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(withAuthority ? "no authority left" : "no trigger authority", refusal);
        Assert.False(agents.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task A_trigger_whose_preset_is_gone_does_not_run()
    {
        var store = Store(active: "Strict");
        var (dispatcher, agents, audit) = Dispatcher(store, preset: "Deleted");

        await dispatcher.OnEventAsync(Event());
        var refusal = await audit.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("'Deleted' does not exist", refusal);
        Assert.False(agents.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task A_whole_run_keeps_its_preset_through_switches_and_edits_until_the_kill_switch_stops_it()
    {
        // A real session, invoker and gateway: every call of the run is decided (and recorded) against the pinned
        // preset, while the operator switches and edits presets between the calls and finally pulls the kill switch.
        var store = Store(active: "Strict");
        var decisions = new MemoryDecisions();
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationGateway>.Instance,
            decisions: decisions, triggers: AgentToolTestHelpers.GrantAll.Instance);
        var invoker = new AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(), GuardrailEngine.CreateDefault(),
            new NoScopes(), gateway: gateway);
        var pinned = PinnedPolicy.Of("Repair", store.Config.Presets.Single(p => p.Name == "Repair").Policy);
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart'", McpPermissionLevels.Write,
            null, TriggerId: "t1");
        var context = new AgentContext("run-1", trigger, AgentOrigin.Trigger, pinned.Policy.Clone(), pinned);
        const string args = "{\"containerId\":\"web\",\"serverId\":\"srv1\"}";
        var provider = new ScriptedProvider(
            (null, new AgentToolCall("c1", Start.McpTool!, args)),
            // Between the calls: the active preset switches to "Repair", which is then tightened to forbid starts.
            (() => { store.ActiveName = "Repair"; store.Presets.Single(p => p.Name == "Repair").Policy = NoStart(); },
                new AgentToolCall("c2", Start.McpTool!, args)),
            // Then the kill switch goes on (it lives in the active preset).
            (() => store.Presets.Single(p => p.Name == "Repair").Policy.ReadOnlyMode = true,
                new AgentToolCall("c3", Start.McpTool!, args)));
        var session = new AgentSession(context, provider, new NoTools(), invoker, GuardrailEngine.CreateDefault(),
            AgentToolTestHelpers.DefaultRegistry(), new AgentSettings { Model = "m", MaxToolIterations = 6 }, "system",
            guardrailStore: store);

        var proposed = new List<GuardrailVerdict>();
        await foreach (var ev in session.SendAsync("go"))
            if (ev is AgentEvent.ToolProposed p) proposed.Add(p.Decision.Verdict);

        Assert.Equal([GuardrailVerdict.Allow, GuardrailVerdict.Allow, GuardrailVerdict.Deny], proposed);
        var recorded = decisions.Records;
        Assert.Equal(3, recorded.Count);
        Assert.All(recorded, r => Assert.Equal("Repair", r.Policy.Preset));
        Assert.Equal([MutationVerdict.Allow, MutationVerdict.Allow, MutationVerdict.Deny], recorded.Select(r => r.Decision.Verdict));
        var started = PolicySnapshot.Of("Repair", Open()).VersionHash;
        Assert.Equal(started, recorded[0].Policy.VersionHash);
        Assert.Equal(started, recorded[1].Policy.VersionHash);     // the edit of "Repair" did not reach the run
        Assert.NotEqual(started, recorded[2].Policy.VersionHash);  // the kill switch did
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mutation_nested_in_a_pinned_run_is_decided_against_the_pinned_preset(bool killSwitch)
    {
        // The trigger's preset protects "web", the live one does not. The trigger runs a tool (here: starting another
        // container) whose execution makes a nested gateway call without a pin of its own — as run_scheduled_task
        // does for the task's container.restart. The nested call must stay under the trigger's preset.
        var guarded = Open();
        guarded.ProtectedResources.Add("web");
        var store = new PresetStore("Live", new GuardrailPreset { Name = "Live", Policy = Open() });
        var decisions = new MemoryDecisions();
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationGateway>.Instance,
            decisions: decisions, triggers: AgentToolTestHelpers.GrantAll.Instance);
        var outer = TriggerCall(PinnedPolicy.Of("Guarded", guarded)) with
        {
            Target = new MutationTarget("container", "c2", "worker", "srv1"),
            Arguments = new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement("worker") },
        };
        var owner = new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com",
            McpPermissionLevels.Admin);
        var nested = new MutationRequest("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Automation, "task:t1", "Scheduled task", McpPermissionLevels.Admin,
                OnBehalfOf: owner),
            MutationSurface.Automation, new MutationTarget("container", "c1", "web", "srv1"),
            new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement("web") });
        MutationOutcome<int>? inner = null;

        var outcome = await gateway.ExecuteAsync(outer, async (_, ct) =>
        {
            if (killSwitch) store.Presets.Single().Policy.ReadOnlyMode = true;
            inner = await gateway.ExecuteAsync(nested, (_, _) => Task.FromResult(1), ct);
            return 0;
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(MutationStatus.Denied, inner!.Status);
        var nestedDecision = decisions.Records.Single(r => r.Capability.Id == "container.restart");
        Assert.Equal("Guarded", nestedDecision.Policy.Preset);
        Assert.Equal(MutationVerdict.Deny, nestedDecision.Decision.Verdict);
        // Outside the run the same call follows the live preset: allowed (it does not protect "web"), unless the
        // kill switch is on.
        var alone = await gateway.ExecuteAsync(nested, (_, _) => Task.FromResult(1));
        Assert.Equal(killSwitch ? MutationStatus.Denied : MutationStatus.Executed, alone.Status);
        Assert.Equal("Live", decisions.Records.Last().Policy.Preset);
    }

    [Fact]
    public async Task A_task_a_person_created_keeps_the_pinned_presets_agent_rules_inside_a_trigger_run()
    {
        // The trigger's preset denies restarting containers. A person created a restart task (owner authority, which
        // alone would be decided by the person's rules); run from inside the trigger's call it is the trigger acting.
        var noRestart = Open();
        noRestart.ToolDenyList.Add(new CapabilityCatalog().Find("container.restart")!.McpTool!);
        var store = new PresetStore("Live", new GuardrailPreset { Name = "Live", Policy = Open() });
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationGateway>.Instance,
            triggers: AgentToolTestHelpers.GrantAll.Instance);
        var nested = PersonsTask("container.restart", "web");
        MutationOutcome<int>? inner = null;

        await gateway.ExecuteAsync(OuterCall(PinnedPolicy.Of("NoRestart", noRestart)), async (_, ct) =>
        {
            inner = await gateway.ExecuteAsync(nested, (_, _) => Task.FromResult(1), ct);
            return 0;
        });

        Assert.Equal(MutationStatus.Denied, inner!.Status);
        Assert.Equal(MutationStatus.Executed, (await gateway.ExecuteAsync(nested, (_, _) => Task.FromResult(1))).Status);
    }

    [Fact]
    public async Task A_cleanup_task_inside_a_trigger_run_protects_what_the_pinned_preset_protects()
    {
        // The live preset protects nothing; the trigger's protects "web", which is stopped: a prune would remove it.
        var guarded = Open();
        guarded.ProtectedResources.Add("web");
        var store = new PresetStore("Live", new GuardrailPreset { Name = "Live", Policy = Open() });
        var docker = new FakeDocker(new ContainerInfo { Id = "c1", Name = "web", State = "exited", ServerId = "srv1" });
        var runner = new CountingRunner();
        var executor = new TaskExecutor(docker, null!, null!, null!, null!, runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskExecutor>.Instance, guardrails: store);
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "prune", TaskType = ScheduledTaskType.Cleanup, ServerId = "srv1",
            CreatedBy = "user:ops@example.com", Config = "{}",
        };
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationGateway>.Instance,
            triggers: AgentToolTestHelpers.GrantAll.Instance);
        (bool Success, string Message) inRun = default;

        await gateway.ExecuteAsync(OuterCall(PinnedPolicy.Of("Guarded", guarded)), async (_, _) =>
        {
            inRun = await executor.ExecuteAsync(task);
            return 0;
        });

        Assert.False(inRun.Success);
        Assert.Contains("web", inRun.Message);
        Assert.Equal(0, runner.Calls);
        await executor.ExecuteAsync(task);    // outside the run the live preset applies: the prune is attempted
        Assert.Equal(1, runner.Calls);
    }

    private static MutationRequest OuterCall(PinnedPolicy pinned) => TriggerCall(pinned) with
    {
        Target = new MutationTarget("container", "c2", "worker", "srv1"),
        Arguments = new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement("worker") },
    };

    private static MutationRequest PersonsTask(string capability, string container) => new(capability,
        new MutationPrincipal(MutationPrincipalKind.Automation, "task:t1", "Scheduled task", McpPermissionLevels.Admin,
            OnBehalfOf: new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com",
                McpPermissionLevels.Admin)),
        MutationSurface.Automation, new MutationTarget("container", "c1", container, "srv1"),
        new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(container) });

    private sealed class CountingRunner : IAutomationMutationRunner
    {
        public int Calls;

        public Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new MutationOutcome<T>("op", MutationStatus.Denied, default, "test", VerificationStatus.NotRun));
        }

        public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            RunAsync<bool>(actor, capabilityId, arguments, async t => { await action(t); return true; }, ct);
    }

    // ---- Helpers -----------------------------------------------------------

    private static NotificationEvent Event() => new()
    {
        EventType = "unhealthy", ContainerId = "c1", ContainerName = "web", ServerId = "srv1",
    };

    private static (AiTriggerDispatcher, CapturingAgents, CapturingAudit) Dispatcher(IGuardrailStore store, string preset,
        TriggerRunAuthority? start = null, bool withAuthority = true)
    {
        var agents = new CapturingAgents();
        var audit = new CapturingAudit();
        var services = new ServiceCollection()
            .AddSingleton<IAgentService>(agents)
            .AddSingleton(store)
            .AddSingleton<IAuditLogService>(audit)
            .AddSingleton<INotificationService>(new NullNotify())
            ;
        if (withAuthority)
            services.AddSingleton<IAiTriggerAuthority>(new FixedRuns(start ?? new TriggerRunAuthority(McpPermissionLevels.Read, null)));
        var sp = services.BuildServiceProvider();
        var triggers = new TriggerList(new AiTrigger
        {
            Id = "t1", Name = "restart", EventTypes = ["unhealthy"], GuardrailPreset = preset,
        });
        var settings = new StaticSettings(new AgentSettings { Enabled = true });
        return (new AiTriggerDispatcher(triggers, sp, settings), agents, audit);
    }

    /// <summary>Starts every run with the given authority (read-only unless a test says otherwise).</summary>
    private sealed class FixedRuns(TriggerRunAuthority start) : IAiTriggerAuthority
    {
        public Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(start);
        public Task<string?> RefusalAsync(string triggerId, Capability capability, MutationTarget target, CancellationToken ct) =>
            Task.FromResult<string?>("read-only");
    }

    private sealed class MemoryDecisions : IPolicyDecisionStore
    {
        public List<PolicyDecisionRecord> Records { get; } = [];

        public Task RecordAsync(PolicyDecisionRecord record, CancellationToken ct)
        {
            lock (Records) Records.Add(record);
            return Task.CompletedTask;
        }
    }

    /// <summary>One tool call per turn; the hook runs before the turn is streamed (between two calls of the run).</summary>
    private sealed class ScriptedProvider(params (Action? Before, AgentToolCall Call)[] turns) : IAgentLlmProvider
    {
        private int _turn;
        public string Id => "scripted";

        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(AgentCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_turn < turns.Length)
            {
                var (before, call) = turns[_turn++];
                before?.Invoke();
                yield return new AgentStreamDelta(ToolCallDelta: call);
                yield return new AgentStreamDelta(Final: AgentStopReason.ToolCalls);
            }
            else yield return new AgentStreamDelta(Final: AgentStopReason.Stop);
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class NoTools : IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => Array.Empty<AgentToolDefinition>();
    }

    /// <summary>The tool itself never runs here; only the decisions are under test.</summary>
    private sealed class NoScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("the tool does not run in this test");
    }

    private sealed class CapturingAgents : IAgentService
    {
        public TaskCompletionSource<AgentContext> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAgentSession> StartSessionAsync(AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null,
            CancellationToken ct = default)
        {
            Started.TrySetResult(context);
            throw new InvalidOperationException("no provider in this test");
        }

        public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAgentSession?>(null);
    }

    private sealed class CapturingAudit : IAuditLogService
    {
        public async Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            await LogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);
            return true;
        }

        public List<string> Refusals { get; } = [];
        public TaskCompletionSource<string> Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            if (!success)
            {
                lock (Refusals) Refusals.Add(details ?? "");
                Refused.TrySetResult(details ?? "");
            }
            return Task.CompletedTask;
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private sealed class TriggerList(params AiTrigger[] triggers) : IAiTriggerStore
    {
        public IReadOnlyList<AiTrigger> Triggers { get; } = triggers;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long Revision => 0;
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class StaticSettings(AgentSettings value) : IOptionsMonitor<AgentSettings>
    {
        public AgentSettings CurrentValue => value;
        public AgentSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<AgentSettings, string?> listener) => null;
    }

    private sealed class NullNotify : INotificationService
    {
        public Task SendAsync(NotificationEvent evt) => Task.CompletedTask;
        public Task SendTestAsync() => Task.CompletedTask;
    }
}
