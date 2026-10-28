using System.Runtime.CompilerServices;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Approvals;
using Whiskers.Services.Agent.Providers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>R1-15 (INV-36, C-08, C-18): an approval from a chat that was closed or lost its connection never resumes
/// the session unattended, and a session waits on unique operation ids, not on provider tool-call ids.</summary>
public class OrphanedApprovalTests
{
    private sealed class Provider(params List<AgentStreamDelta>[] turns) : IAgentLlmProvider
    {
        private readonly Queue<List<AgentStreamDelta>> _turns = new(turns);
        public string Id => "fake";

        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
            AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var turn = _turns.Count > 0 ? _turns.Dequeue() : [new(Final: AgentStopReason.Stop)];
            foreach (var d in turn) yield return d;
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class Catalog : IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => Array.Empty<AgentToolDefinition>();
    }

    private sealed class Invoker : IAgentToolInvoker
    {
        public int Calls;
        public Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AgentToolResult(call.Id, "OK", false,
                new GuardrailDecision(GuardrailVerdict.Allow, "", Array.Empty<string>())));
        }
    }

    private sealed class ConfirmEverything : IAgentGuardrailEngine
    {
        public GuardrailDecision Evaluate(GuardrailRequest request) =>
            new(GuardrailVerdict.Confirm, "Writes need a human.", Array.Empty<string>());
    }

    private sealed class Policy : IGuardrailStore
    {
        public GuardrailPolicy Current { get; set; } = GuardrailPolicy.SafeDefault();
        public GuardrailConfig Config => GuardrailConfig.SafeDefault();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class Notify : INotificationService
    {
        public Task SendAsync(NotificationEvent evt) => Task.CompletedTask;
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private const string Owner = "alice@example.com";

    private static readonly AgentContext Context = new("chat-1",
        new AgentPrincipal(AgentPrincipalKind.WebUser, Owner, McpPermissionLevels.Admin, null, UserEmail: Owner),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static readonly ApprovalResolver Approver =
        new(PrincipalIds.User("bob@example.com"), McpPermissionLevels.Admin, "bob@example.com");

    private static List<AgentStreamDelta> ToolTurn(string providerCallId) =>
    [
        new(ToolCallDelta: new AgentToolCall(providerCallId, "stop_container", "{\"containerId\":\"web\"}")),
        new(Final: AgentStopReason.ToolCalls),
    ];

    private static List<AgentStreamDelta> TextTurn(string text) => [new(TextDelta: text), new(Final: AgentStopReason.Stop)];

    private static AgentSession Session(Invoker invoker, params List<AgentStreamDelta>[] turns) =>
        new(Context, new Provider(turns), new Catalog(), invoker, new ConfirmEverything(), AgentToolTestHelpers.DefaultRegistry(),
            new AgentSettings { Model = "x", MaxToolIterations = 4 }, "system");

    /// <summary>Runs the chat until it asks for an approval, raising it as the chat page does.</summary>
    private static async Task<Approval> RunToApprovalAsync(IAsyncEnumerator<AgentEvent> run, AgentSession session,
        IApprovalCoordinator coordinator)
    {
        while (await run.MoveNextAsync())
            if (run.Current is AgentEvent.ConfirmationRequired c)
                return await coordinator.RaiseAsync(session, c.Call, Context, c.Reason, c.Decision);
        throw new InvalidOperationException("The run ended without asking for an approval.");
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerator<AgentEvent> run)
    {
        var events = new List<AgentEvent>();
        try
        {
            while (await run.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))) events.Add(run.Current);
        }
        catch (OperationCanceledException) { }
        return events;
    }

    [Theory]
    [InlineData(true)]    // the page was left / the connection is gone: the component is disposed
    [InlineData(false)]   // "Clear chat"
    public async Task An_approval_from_an_ended_chat_cannot_resume_it(bool disposed)
    {
        var store = new ApprovalStore();
        var invoker = new Invoker();
        var session = Session(invoker, ToolTurn("call_0"), TextTurn("done"));
        var lifetime = new AgentChatLifetime(store);
        lifetime.Attach(session);
        var coordinator = new ApprovalCoordinator(store, new Notify(), new Policy());

        await using var run = session.SendAsync("stop web", ct: lifetime.Token).GetAsyncEnumerator();
        var approval = await RunToApprovalAsync(run, session, coordinator);
        Assert.True(approval.Interactive);

        if (disposed) await lifetime.DisposeAsync();
        else await lifetime.EndAsync();

        // The approval is gone from the list, and approving it elsewhere changes nothing.
        Assert.Equal(ApprovalStatus.Cancelled, approval.Status);
        Assert.Empty(store.GetPending());
        Assert.Equal(ApprovalDecisionResult.NotPending, await store.DecideAsync(approval.Id, true, Approver));

        // The run stops instead of going on with a "rejected" result.
        var rest = await DrainAsync(run);
        Assert.DoesNotContain(rest, e => e is AgentEvent.ToolExecuted);
        Assert.Equal(0, invoker.Calls);
    }

    [Fact]
    public async Task While_the_chat_is_open_its_approval_resumes_it()
    {
        var store = new ApprovalStore();
        var invoker = new Invoker();
        var session = Session(invoker, ToolTurn("call_0"), TextTurn("done"));
        await using var lifetime = new AgentChatLifetime(store);
        lifetime.Attach(session);
        var coordinator = new ApprovalCoordinator(store, new Notify(), new Policy());

        await using var run = session.SendAsync("stop web", ct: lifetime.Token).GetAsyncEnumerator();
        var approval = await RunToApprovalAsync(run, session, coordinator);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true, Approver));

        var rest = await DrainAsync(run);
        Assert.Contains(rest, e => e is AgentEvent.ToolExecuted { Result.IsError: false });
        Assert.Equal(1, invoker.Calls);
    }

    [Fact]
    public async Task A_new_session_after_clearing_the_chat_is_not_affected()
    {
        var store = new ApprovalStore();
        await using var lifetime = new AgentChatLifetime(store);
        var coordinator = new ApprovalCoordinator(store, new Notify(), new Policy());

        var first = Session(new Invoker(), ToolTurn("call_0"));
        lifetime.Attach(first);
        await using var firstRun = first.SendAsync("one", ct: lifetime.Token).GetAsyncEnumerator();
        var old = await RunToApprovalAsync(firstRun, first, coordinator);
        await lifetime.EndAsync();

        var invoker = new Invoker();
        var second = new AgentSession(Context with { SessionId = "chat-2" }, new Provider(ToolTurn("call_0"), TextTurn("done")),
            new Catalog(), invoker, new ConfirmEverything(), AgentToolTestHelpers.DefaultRegistry(),
            new AgentSettings { Model = "x", MaxToolIterations = 4 }, "system");
        lifetime.Attach(second);
        await using var secondRun = second.SendAsync("two", ct: lifetime.Token).GetAsyncEnumerator();
        var current = await RunToApprovalAsync(secondRun, second, coordinator);

        Assert.Equal(ApprovalStatus.Cancelled, old.Status);
        Assert.Equal(ApprovalStatus.Pending, current.Status);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(current.Id, true, Approver));
        await DrainAsync(secondRun);
        Assert.Equal(1, invoker.Calls);
    }

    [Fact]
    public async Task A_decision_addressed_by_a_reused_provider_call_id_never_reaches_a_later_call()
    {
        // Providers reuse tool-call ids across turns (Gemini "call_0", fallback ids). The second call carries the
        // same provider id as the first; only its own approval may decide it.
        var store = new ApprovalStore();
        var invoker = new Invoker();
        var session = Session(invoker, ToolTurn("call_0"), ToolTurn("call_0"), TextTurn("done"));
        await using var lifetime = new AgentChatLifetime(store);
        lifetime.Attach(session);
        var coordinator = new ApprovalCoordinator(store, new Notify(), new Policy());

        await using var run = session.SendAsync("stop web twice", ct: lifetime.Token).GetAsyncEnumerator();
        var first = await RunToApprovalAsync(run, session, coordinator);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(first.Id, false, Approver));
        var second = await RunToApprovalAsync(run, session, coordinator);

        // Unique ids, although the provider's id repeats.
        Assert.Equal("call_0", first.ToolCallId);
        Assert.Equal("call_0", second.ToolCallId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);

        // A stale "yes" by provider id, then the real decision ("no") by the approval.
        await session.ResolveConfirmationAsync("call_0", true);
        Assert.Equal(ApprovalDecisionResult.NotPending, await store.DecideAsync(first.Id, true, Approver));
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(second.Id, false, Approver));

        await DrainAsync(run);
        Assert.Equal(0, invoker.Calls);
    }

    [Fact]
    public async Task A_restart_cancels_the_pending_approvals_of_chats()
    {
        var persistence = new InMemoryApprovalPersistence();
        var before = new ApprovalStore(persistence: persistence);
        var invoker = new Invoker();
        var session = Session(invoker, ToolTurn("call_0"));
        var coordinator = new ApprovalCoordinator(before, new Notify(), new Policy());
        await using var run = session.SendAsync("stop web").GetAsyncEnumerator();
        var approval = await RunToApprovalAsync(run, session, coordinator);

        var after = new ApprovalStore(persistence: persistence);
        await after.LoadAsync();

        Assert.Equal(ApprovalStatus.Cancelled, after.Get(approval.Id)!.Status);
        Assert.Equal(ApprovalDecisionResult.NotPending, await after.DecideAsync(approval.Id, true, Approver));
        Assert.Equal(0, invoker.Calls);
    }
}
