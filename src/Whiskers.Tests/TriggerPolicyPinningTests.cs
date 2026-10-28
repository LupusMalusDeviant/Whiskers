using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
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
    public async Task A_trigger_whose_preset_is_gone_does_not_run()
    {
        var store = Store(active: "Strict");
        var (dispatcher, agents, audit) = Dispatcher(store, preset: "Deleted");

        await dispatcher.OnEventAsync(Event());
        var refusal = await audit.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("'Deleted' does not exist", refusal);
        Assert.False(agents.Started.Task.IsCompleted);
    }

    // ---- Helpers -----------------------------------------------------------

    private static NotificationEvent Event() => new()
    {
        EventType = "unhealthy", ContainerId = "c1", ContainerName = "web", ServerId = "srv1",
    };

    private static (AiTriggerDispatcher, CapturingAgents, CapturingAudit) Dispatcher(IGuardrailStore store, string preset)
    {
        var agents = new CapturingAgents();
        var audit = new CapturingAudit();
        var sp = new ServiceCollection()
            .AddSingleton<IAgentService>(agents)
            .AddSingleton(store)
            .AddSingleton<IAuditLogService>(audit)
            .AddSingleton<INotificationService>(new NullNotify())
            .BuildServiceProvider();
        var triggers = new TriggerList(new AiTrigger
        {
            Id = "t1", Name = "restart", EventTypes = ["unhealthy"], GuardrailPreset = preset,
        });
        var settings = new StaticSettings(new AgentSettings { Enabled = true });
        return (new AiTriggerDispatcher(triggers, sp, settings), agents, audit);
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
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor) => Task.CompletedTask;
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
