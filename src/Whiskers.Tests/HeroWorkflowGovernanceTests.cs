using System.Runtime.CompilerServices;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Approvals;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Agent.Providers;
using Whiskers.Services.Notifications;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>WP-05 — the governance hero workflow end to end:
/// Observe → Propose → Check policy → Request approval → Execute → Verify → Audit.
/// Proves a Confirm verdict is NOT executed before a human approval, executes EXACTLY ONCE and only
/// with the originally-approved arguments, and that one correlation id ties the call, the approval,
/// the recorded history and the raised notification together. Plus the negative paths: rejected /
/// expired / double-approved never execute, and secrets never reach the approval or the history.</summary>
public class HeroWorkflowGovernanceTests
{
    // ---- fakes -------------------------------------------------------------

    private sealed class FakeProvider : IAgentLlmProvider
    {
        private readonly Queue<List<AgentStreamDelta>> _turns;
        public FakeProvider(params List<AgentStreamDelta>[] turns) => _turns = new(turns);
        public string Id => "fake";
        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
            AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = _turns.Count > 0 ? _turns.Dequeue() : new() { new AgentStreamDelta(Final: AgentStopReason.Stop) };
            foreach (var d in turn) yield return d;
            await Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class FakeCatalog : IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => Array.Empty<AgentToolDefinition>();
    }

    /// <summary>Records the exact call it was asked to execute — so the test can assert it ran once,
    /// with the approved arguments, carrying the same correlation id.</summary>
    private sealed class CapturingInvoker : IAgentToolInvoker
    {
        public int Calls;
        public AgentToolCall? LastCall;
        public Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
        {
            Calls++;
            LastCall = call;
            return Task.FromResult(new AgentToolResult(call.Id, "OK", false,
                new GuardrailDecision(GuardrailVerdict.Allow, "", Array.Empty<string>())));
        }
    }

    private sealed class ConfirmEngine : IAgentGuardrailEngine
    {
        public GuardrailDecision Evaluate(GuardrailRequest request) =>
            new(GuardrailVerdict.Confirm, "write action requires confirmation", new[] { "tool-mode:confirm" });
    }

    private sealed class FakeGuardrailStore : IGuardrailStore
    {
        public GuardrailPolicy Current => GuardrailPolicy.SafeDefault();
        public GuardrailConfig Config { get; } = new() { ActivePreset = "Safe operations" };
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class CapturingNotify : INotificationService
    {
        public NotificationEvent? Last;
        public Task SendAsync(NotificationEvent evt) { Last = evt; return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class CapturingCallLog : IMcpCallLogStore
    {
        public readonly List<McpToolCallEntity> Entries = new();
        public Task RecordAsync(McpToolCallEntity entry) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<List<McpToolCallEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actor = null,
            string? tool = null, string? verdict = null, bool writesOnly = false, DateTime? since = null) =>
            Task.FromResult(Entries);
        public Task<int> CountAsync(string? actor = null, string? tool = null, string? verdict = null,
            bool writesOnly = false, DateTime? since = null) => Task.FromResult(Entries.Count);
    }

    /// <summary>A scope factory that must never be used — proves a Deny/short-circuit path did not execute.</summary>
    private sealed class NoExecScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException("must not execute");
    }

    // ---- helpers -----------------------------------------------------------

    private static readonly AgentToolRegistry Registry = AgentToolTestHelpers.DefaultRegistry();

    private static AgentContext Context() => new("s1",
        new AgentPrincipal(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null, UserEmail: "admin@x"),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static AgentSession Session(FakeProvider provider, IAgentToolInvoker invoker, IAgentGuardrailEngine engine) =>
        new(Context(), provider, new FakeCatalog(), invoker, engine, Registry,
            new AgentSettings { Model = "x", MaxToolIterations = 4 }, "system");

    private static List<AgentStreamDelta> ToolTurn(string name, string args, string id = "c1") => new()
    {
        new AgentStreamDelta(ToolCallDelta: new AgentToolCall(id, name, args)),
        new AgentStreamDelta(Final: AgentStopReason.ToolCalls),
    };

    private const string RestartArgs = "{\"serverId\":\"srv-1\",\"container\":\"web\"}";

    /// <summary>Drives the session to the Confirm pause, raises a real approval, and returns the pieces
    /// the test asserts on. <paramref name="decide"/> resolves the approval (true=approve) — or leave it
    /// null to never resolve.</summary>
    private static async Task<(CapturingInvoker invoker, Approval approval, ApprovalStore store, CapturingNotify notify)>
        RunToConfirm(Func<ApprovalStore, Approval, Task>? decide, TimeSpan? ttl = null)
    {
        var invoker = new CapturingInvoker();
        var session = Session(new FakeProvider(ToolTurn("restart_container", RestartArgs)), invoker, new ConfirmEngine());
        var store = new ApprovalStore(approvalTtl: ttl);
        var notify = new CapturingNotify();
        var coord = new ApprovalCoordinator(store, notify, new FakeGuardrailStore());

        // Safety backstop: a governance test must never hang. If a path deliberately leaves the
        // approval unresolved (e.g. rejected-by-level, or "card only"), we cancel to end the session's
        // await instead of blocking forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Approval? approval = null;
        await foreach (var e in session.SendAsync("restart the unhealthy container", ct: cts.Token))
        {
            if (e is AgentEvent.ConfirmationRequired cr)
            {
                approval = await coord.RaiseAsync(session, cr.Call, Context(), cr.Reason, cr.Decision);
                Assert.Equal(0, invoker.Calls);                       // NOT executed before approval
                if (decide is not null) await decide(store, approval);
                // If the decision did not drive the approval out of Pending (never resolved, or a
                // rejected-by-level attempt), unblock the waiting session so the stream can end.
                if (approval.Status == ApprovalStatus.Pending) cts.Cancel();
            }
        }
        Assert.NotNull(approval);
        return (invoker, approval!, store, notify);
    }

    // ---- the hero workflow -------------------------------------------------

    [Fact]
    public async Task Confirm_requires_approval_then_executes_once_with_a_preserved_correlation_id()
    {
        var (invoker, approval, _, notify) =
            await RunToConfirm((store, a) => store.ResolveAsync(a.Id, approved: true, "admin@x", "admin"));

        // Executed exactly once...
        Assert.Equal(1, invoker.Calls);
        // ...with the originally-approved arguments (not re-supplied at approval time)...
        Assert.Equal(RestartArgs, invoker.LastCall!.ArgumentsJson);
        // ...and one correlation id ties call → approval → notification.
        Assert.False(string.IsNullOrEmpty(approval.CorrelationId));
        Assert.Equal(invoker.LastCall!.CorrelationId, approval.CorrelationId);
        Assert.Equal(approval.CorrelationId, notify.Last!.CorrelationId);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
    }

    [Fact]
    public async Task Approval_card_carries_risk_target_preset_and_matched_rule()
    {
        var (_, approval, _, _) = await RunToConfirm(decide: null);

        Assert.Equal("medium", approval.RiskLevel);        // restart_container is a write tool
        Assert.Equal("srv-1", approval.TargetServer);
        Assert.Equal("web", approval.TargetWorkload);
        Assert.Equal("Safe operations", approval.GuardrailPreset);
        Assert.Contains("tool-mode:confirm", approval.GuardrailRule!);
    }

    [Fact]
    public async Task Rejected_approval_never_executes()
    {
        var (invoker, approval, _, _) =
            await RunToConfirm((store, a) => store.ResolveAsync(a.Id, approved: false, "admin@x", "admin"));

        Assert.Equal(0, invoker.Calls);
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
    }

    [Fact]
    public async Task Expired_approval_never_executes()
    {
        // A zero TTL makes the approval overdue the instant it is created; the session's await ends when
        // the store sweeps it to Expired and resolves the waiter with "denied".
        var (invoker, approval, store, _) =
            await RunToConfirm((s, a) => { s.GetPending(); return Task.CompletedTask; }, ttl: TimeSpan.Zero);

        Assert.Equal(0, invoker.Calls);
        Assert.Equal(ApprovalStatus.Expired, approval.Status);
    }

    [Fact]
    public async Task Double_approval_executes_at_most_once()
    {
        var (invoker, approval, _, _) = await RunToConfirm(async (store, a) =>
        {
            Assert.True(await store.ResolveAsync(a.Id, true, "admin@x", "admin"));
            Assert.False(await store.ResolveAsync(a.Id, true, "admin@x", "admin"));  // second is a no-op
        });

        Assert.Equal(1, invoker.Calls);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
    }

    [Fact]
    public async Task A_lower_privileged_user_cannot_approve_a_higher_level_action()
    {
        // restart_container is "write"; a read-level resolver may not approve it → no execution.
        var (invoker, approval, _, _) = await RunToConfirm(async (store, a) =>
        {
            Assert.False(await store.ResolveAsync(a.Id, true, "viewer@x", resolverLevel: "read"));
        });

        Assert.Equal(0, invoker.Calls);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);   // still awaiting a valid decision
    }

    // ---- recorded history: correlation + redaction -------------------------

    [Fact]
    public async Task Recorded_history_carries_the_call_correlation_id_and_redacts_secrets()
    {
        // Deny path: the invoker records the call (with correlation id + redacted args) WITHOUT executing.
        var callLog = new CapturingCallLog();
        var invoker = new AgentToolInvoker(Registry, GuardrailEngine.CreateDefault(), new NoExecScopeFactory(), callLog);
        var call = new AgentToolCall("x", "execute_command", "{\"command\":\"echo token=SUPERSECRET\"}");

        var ctx = new AgentContext("s1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Read, null, UserEmail: "t@x"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        var result = await invoker.InvokeAsync(call, ctx);   // read principal → execute_command denied

        Assert.True(result.IsError);
        var entry = Assert.Single(callLog.Entries);
        Assert.Equal(call.CorrelationId, entry.CorrelationId);          // correlation preserved into history
        Assert.DoesNotContain("SUPERSECRET", entry.ParamsJson ?? "");   // secret redacted before persistence
    }
}
