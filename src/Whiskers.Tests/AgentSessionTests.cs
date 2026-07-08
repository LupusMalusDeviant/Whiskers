using System.Runtime.CompilerServices;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class AgentSessionTests
{
    // ---- Fakes -------------------------------------------------------------

    private sealed class FakeProvider : IAgentLlmProvider
    {
        private readonly Queue<List<AgentStreamDelta>> _turns;
        public FakeProvider(params List<AgentStreamDelta>[] turns) => _turns = new Queue<List<AgentStreamDelta>>(turns);
        public string Id => "fake";

        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
            AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = _turns.Count > 0
                ? _turns.Dequeue()
                : new List<AgentStreamDelta> { new(Final: AgentStopReason.Stop) };
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

    private sealed class FakeInvoker : IAgentToolInvoker
    {
        public int Calls;
        public Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AgentToolResult(call.Id, "OK", false,
                new GuardrailDecision(GuardrailVerdict.Allow, "", Array.Empty<string>())));
        }
    }

    private sealed class FakeEngine : IAgentGuardrailEngine
    {
        public GuardrailDecision Decision = new(GuardrailVerdict.Allow, "ok", Array.Empty<string>());
        public GuardrailDecision Evaluate(GuardrailRequest request) => Decision;
    }

    private sealed class FakeGuardrailStore : IGuardrailStore
    {
        public FakeGuardrailStore(GuardrailPolicy current) => Current = current;
        public GuardrailPolicy Current { get; set; }
        public GuardrailConfig Config => new();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    // ---- Helpers -----------------------------------------------------------

    private static readonly AgentToolRegistry Registry = new();

    private static AgentContext Context() => new("s1",
        new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Admin, null, UserEmail: "t@x"),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static AgentSession Session(FakeProvider provider, FakeInvoker invoker, FakeEngine engine) =>
        new(Context(), provider, new FakeCatalog(), invoker, engine, Registry,
            new AgentSettings { Model = "x", MaxToolIterations = 4 }, "system");

    private static async Task<List<AgentEvent>> Collect(AgentSession session, string msg, bool autoConfirm = false)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in session.SendAsync(msg))
        {
            events.Add(e);
            if (autoConfirm && e is AgentEvent.ConfirmationRequired cr)
                await session.ResolveConfirmationAsync(cr.Call.Id, true);
        }
        return events;
    }

    private static List<AgentStreamDelta> ToolTurn(string name) => new()
    {
        new AgentStreamDelta(ToolCallDelta: new AgentToolCall("c1", name, "{}")),
        new AgentStreamDelta(Final: AgentStopReason.ToolCalls),
    };

    private static List<AgentStreamDelta> TextTurn(string text) => new()
    {
        new AgentStreamDelta(TextDelta: text),
        new AgentStreamDelta(Final: AgentStopReason.Stop),
    };

    // ---- Tests -------------------------------------------------------------

    [Fact]
    public async Task Text_only_turn_completes_without_tools()
    {
        var session = Session(new FakeProvider(TextTurn("Hallo")), new FakeInvoker(), new FakeEngine());
        var events = await Collect(session, "hi");

        Assert.Contains(events, e => e is AgentEvent.AssistantDelta a && a.Text == "Hallo");
        Assert.Contains(events, e => e is AgentEvent.TurnCompleted);
        Assert.DoesNotContain(events, e => e is AgentEvent.ToolProposed);
    }

    [Fact]
    public async Task Allowed_tool_executes_and_loop_continues()
    {
        var invoker = new FakeInvoker();
        var session = Session(new FakeProvider(ToolTurn("stop_container"), TextTurn("fertig")), invoker,
            new FakeEngine { Decision = new(GuardrailVerdict.Allow, "ok", Array.Empty<string>()) });

        var events = await Collect(session, "stop it");

        Assert.Equal(1, invoker.Calls);
        Assert.Contains(events, e => e is AgentEvent.ToolExecuted t && !t.Result.IsError);
        Assert.Contains(events, e => e is AgentEvent.AssistantDelta a && a.Text == "fertig");
    }

    [Fact]
    public async Task Denied_tool_is_not_executed()
    {
        var invoker = new FakeInvoker();
        var session = Session(new FakeProvider(ToolTurn("execute_command")), invoker,
            new FakeEngine { Decision = new(GuardrailVerdict.Deny, "verboten", new[] { "tool-deny-list" }) });

        var events = await Collect(session, "run");

        Assert.Equal(0, invoker.Calls);
        Assert.Contains(events, e => e is AgentEvent.ToolExecuted t && t.Result.IsError);
    }

    [Fact]
    public async Task Confirm_then_approve_executes()
    {
        var invoker = new FakeInvoker();
        var session = Session(new FakeProvider(ToolTurn("stop_container"), TextTurn("ok")), invoker,
            new FakeEngine { Decision = new(GuardrailVerdict.Confirm, "bitte bestätigen", new[] { "confirmation" }) });

        var events = await Collect(session, "stop it", autoConfirm: true);

        Assert.Contains(events, e => e is AgentEvent.ConfirmationRequired);
        Assert.Equal(1, invoker.Calls);
        Assert.Contains(events, e => e is AgentEvent.ToolExecuted t && !t.Result.IsError);
    }

    [Fact]
    public async Task Seed_history_precedes_new_turn()
    {
        var seed = new[]
        {
            new AgentMessage(AgentRole.User, "frühere frage"),
            new AgentMessage(AgentRole.Assistant, "frühere antwort"),
        };
        var session = new AgentSession(Context(), new FakeProvider(TextTurn("neue antwort")), new FakeCatalog(),
            new FakeInvoker(), new FakeEngine(), Registry,
            new AgentSettings { Model = "x", MaxToolIterations = 2 }, "system", seedHistory: seed);

        Assert.Equal(2, session.History.Count);   // vor dem Senden

        await foreach (var _ in session.SendAsync("neu")) { }

        var h = session.History;
        Assert.Equal("frühere frage", h[0].Text);
        Assert.Equal("frühere antwort", h[1].Text);
        Assert.Contains(h, m => m.Role == AgentRole.User && m.Text == "neu");
        Assert.Contains(h, m => m.Role == AgentRole.Assistant && m.Text == "neue antwort");
    }

    [Fact] // MIT-33: the LIVE guardrail policy (store) reaches an open session, not the frozen context policy
    public async Task Live_store_policy_action_limit_is_enforced()
    {
        var store = new FakeGuardrailStore(new GuardrailPolicy { MaxActionsPerSession = 0 });
        var invoker = new FakeInvoker();
        var session = new AgentSession(Context(), new FakeProvider(ToolTurn("stop_container"), TextTurn("done")),
            new FakeCatalog(), invoker, new FakeEngine(), Registry,
            new AgentSettings { Model = "x", MaxToolIterations = 2 }, "system", seedHistory: null, guardrailStore: store);

        var events = await Collect(session, "go");

        Assert.Equal(0, invoker.Calls); // live limit=0 blocks execution although the engine allowed it
        Assert.Contains(events, e => e is AgentEvent.ToolExecuted t && t.Result.IsError);
    }

    [Fact] // MIT-32: one active run per session
    public async Task Second_concurrent_send_is_rejected()
    {
        var session = Session(new FakeProvider(TextTurn("A")), new FakeInvoker(), new FakeEngine());

        // Start the first run and advance it once so it is active (suspended mid-stream), not yet finished.
        var first = session.SendAsync("first").GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        // A second send while the first is still active must be rejected, not interleaved.
        var second = new List<AgentEvent>();
        await foreach (var ev in session.SendAsync("second")) second.Add(ev);
        Assert.Contains(second, ev => ev is AgentEvent.Failed);

        // Drain the first so it releases the guard.
        while (await first.MoveNextAsync()) { }
        await first.DisposeAsync();
    }

    [Fact]
    public async Task Confirm_then_deny_skips_execution()
    {
        var invoker = new FakeInvoker();
        var session = Session(new FakeProvider(ToolTurn("stop_container"), TextTurn("ok")), invoker,
            new FakeEngine { Decision = new(GuardrailVerdict.Confirm, "bitte bestätigen", new[] { "confirmation" }) });

        var events = new List<AgentEvent>();
        await foreach (var e in session.SendAsync("stop it"))
        {
            events.Add(e);
            if (e is AgentEvent.ConfirmationRequired cr)
                await session.ResolveConfirmationAsync(cr.Call.Id, false);
        }

        Assert.Equal(0, invoker.Calls);
        Assert.Contains(events, e => e is AgentEvent.ToolExecuted t && t.Result.IsError);
    }
}
