using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using ServerWatch.Configuration;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent.Guardrails;
using ServerWatch.Services.Agent.Providers;

namespace ServerWatch.Services.Agent;

/// <summary>Drives the agentic loop for a conversation: stream the provider → run tool calls through
/// the guardrail gate → pause on Confirm (await ResolveConfirmationAsync) → feed results
/// back → until Stop or MaxToolIterations. The rate limit (MaxActionsPerSession) is enforced
/// here (stateful, hence not in the stateless engine).</summary>
public sealed class AgentSession : IAgentSession
{
    private readonly AgentContext _context;
    private readonly IAgentLlmProvider _provider;
    private readonly IAgentToolCatalog _catalog;
    private readonly IAgentToolInvoker _invoker;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IGuardrailStore? _guardrailStore;
    private readonly IAgentToolRegistry _registry;
    private readonly AgentSettings _settings;
    private readonly string _systemPrompt;

    private readonly List<AgentMessage> _history = new();
    private readonly object _historyLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private int _actionCount;
    private int _sending;

    public string SessionId => _context.SessionId;
    public AgentRunState State { get; private set; } = AgentRunState.Idle;

    public AgentSession(
        AgentContext context, IAgentLlmProvider provider, IAgentToolCatalog catalog,
        IAgentToolInvoker invoker, IAgentGuardrailEngine guardrails, IAgentToolRegistry registry,
        AgentSettings settings, string systemPrompt, IReadOnlyList<AgentMessage>? seedHistory = null,
        IGuardrailStore? guardrailStore = null)
    {
        _context = context;
        _provider = provider;
        _catalog = catalog;
        _invoker = invoker;
        _guardrails = guardrails;
        _guardrailStore = guardrailStore;
        _registry = registry;
        _settings = settings;
        _systemPrompt = systemPrompt;
        if (seedHistory is { Count: > 0 }) _history.AddRange(seedHistory);
    }

    public IReadOnlyList<AgentMessage> History => HistorySnapshot();

    private void AddHistory(AgentMessage m) { lock (_historyLock) { _history.Add(m); } }
    private List<AgentMessage> HistorySnapshot() { lock (_historyLock) { return _history.ToList(); } }

    public Task ResolveConfirmationAsync(string toolCallId, bool approved, CancellationToken ct = default)
    {
        if (_pending.TryGetValue(toolCallId, out var tcs))
            tcs.TrySetResult(approved);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage, string? imageBase64 = null, string? imageMediaType = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One active run per session — a second concurrent SendAsync is rejected so two runs can never
        // interleave _history (which would corrupt it / desync the provider transcript).
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
        {
            yield return new AgentEvent.Failed("Diese Session ist bereits aktiv.");
            yield break;
        }
        try
        {
            await foreach (var ev in RunAsync(userMessage, imageBase64, imageMediaType, ct))
                yield return ev;
        }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }

    private async IAsyncEnumerable<AgentEvent> RunAsync(
        string userMessage, string? imageBase64, string? imageMediaType,
        [EnumeratorCancellation] CancellationToken ct)
    {
        AddHistory(new AgentMessage(AgentRole.User, userMessage,
            ImageBase64: string.IsNullOrEmpty(imageBase64) ? null : imageBase64,
            ImageMediaType: imageMediaType ?? "image/png"));
        var tools = _catalog.GetVisibleTools(_context);
        var maxIterations = Math.Max(1, _settings.MaxToolIterations);

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            State = AgentRunState.Thinking;
            var request = new AgentCompletionRequest(
                _settings.Model, _systemPrompt, HistorySnapshot(), tools, _settings.MaxTokens, 0.2, AgentToolChoice.Auto);

            var text = new StringBuilder();
            var calls = new List<AgentToolCall>();
            var stop = AgentStopReason.Stop;

            await foreach (var delta in _provider.StreamAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(delta.TextDelta))
                {
                    text.Append(delta.TextDelta);
                    yield return new AgentEvent.AssistantDelta(delta.TextDelta!);
                }
                if (delta.ToolCallDelta is { } tc) calls.Add(tc);
                if (delta.Final is { } final) stop = final;
            }

            AddHistory(new AgentMessage(AgentRole.Assistant,
                text.Length > 0 ? text.ToString() : null,
                calls.Count > 0 ? calls : null));

            if (calls.Count == 0)
            {
                State = AgentRunState.Done;
                yield return new AgentEvent.TurnCompleted(stop, new AgentUsage(0, 0));
                yield break;
            }

            State = AgentRunState.Running;
            foreach (var call in calls)
            {
                var decision = Evaluate(call);
                yield return new AgentEvent.ToolProposed(call, decision);

                if (decision.Verdict == GuardrailVerdict.Deny)
                {
                    yield return RecordResult(call, new AgentToolResult(call.Id, $"Blockiert: {decision.Reason}", true, decision));
                    continue;
                }

                if (decision.Verdict == GuardrailVerdict.Confirm)
                {
                    // Register tcs BEFORE the yield so an immediate Resolve finds it.
                    State = AgentRunState.AwaitingConfirmation;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[call.Id] = tcs;

                    bool? approved;
                    try
                    {
                        yield return new AgentEvent.ConfirmationRequired(call, decision.Reason);
                        try { approved = await tcs.Task.WaitAsync(ct); }
                        catch (OperationCanceledException) { approved = null; }
                    }
                    finally
                    {
                        // Runs even if the enumerator is disposed mid-confirmation → no leaked _pending entry.
                        _pending.TryRemove(call.Id, out _);
                    }
                    State = AgentRunState.Running;

                    if (approved is null) yield break;             // abgebrochen
                    if (approved == false)
                    {
                        yield return RecordResult(call, new AgentToolResult(call.Id, "Vom Benutzer abgelehnt.", true, decision));
                        continue;
                    }
                }

                if (_actionCount >= LivePolicy.MaxActionsPerSession)
                {
                    yield return RecordResult(call, new AgentToolResult(call.Id,
                        $"Aktions-Limit dieser Session erreicht ({LivePolicy.MaxActionsPerSession}).", true, decision));
                    continue;
                }

                var result = await _invoker.InvokeAsync(call, LiveContext, ct);
                _actionCount++;
                yield return RecordResult(call, result);
            }
        }

        State = AgentRunState.Done;
        yield return new AgentEvent.TurnCompleted(AgentStopReason.Length, new AgentUsage(0, 0));
    }

    private AgentEvent RecordResult(AgentToolCall call, AgentToolResult result)
    {
        AddHistory(new AgentMessage(AgentRole.Tool, result.Content,
            ToolCallId: call.Id, IsError: result.IsError, ToolName: call.Name));
        return new AgentEvent.ToolExecuted(result);
    }

    // The guardrail policy in effect right now: the live store (so a Read-only kill-switch reaches an
    // already-open session) or — absent a store (pinned-preset trigger runs / tests) — the session context.
    private GuardrailPolicy LivePolicy => _guardrailStore?.Current ?? _context.Policy;
    private AgentContext LiveContext => _context with { Policy = LivePolicy };

    private GuardrailDecision Evaluate(AgentToolCall call)
    {
        if (!_registry.Tools.TryGetValue(call.Name, out var entry))
            return new GuardrailDecision(GuardrailVerdict.Allow,
                "Unbekanntes Tool — der Invoker meldet den Fehler.", Array.Empty<string>());

        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);
        return _guardrails.Evaluate(new GuardrailRequest(entry.Name, entry.RequiredLevel, args, LiveContext));
    }
}
