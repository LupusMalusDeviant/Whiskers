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
    private const int MaxTokens = 1024;

    private readonly AgentContext _context;
    private readonly IAgentLlmProvider _provider;
    private readonly IAgentToolCatalog _catalog;
    private readonly IAgentToolInvoker _invoker;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IAgentToolRegistry _registry;
    private readonly AgentSettings _settings;
    private readonly string _systemPrompt;

    private readonly List<AgentMessage> _history = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private int _actionCount;

    public string SessionId => _context.SessionId;
    public AgentRunState State { get; private set; } = AgentRunState.Idle;

    public AgentSession(
        AgentContext context, IAgentLlmProvider provider, IAgentToolCatalog catalog,
        IAgentToolInvoker invoker, IAgentGuardrailEngine guardrails, IAgentToolRegistry registry,
        AgentSettings settings, string systemPrompt, IReadOnlyList<AgentMessage>? seedHistory = null)
    {
        _context = context;
        _provider = provider;
        _catalog = catalog;
        _invoker = invoker;
        _guardrails = guardrails;
        _registry = registry;
        _settings = settings;
        _systemPrompt = systemPrompt;
        if (seedHistory is { Count: > 0 }) _history.AddRange(seedHistory);
    }

    public IReadOnlyList<AgentMessage> History => _history.ToList();

    public Task ResolveConfirmationAsync(string toolCallId, bool approved, CancellationToken ct = default)
    {
        if (_pending.TryGetValue(toolCallId, out var tcs))
            tcs.TrySetResult(approved);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(new AgentMessage(AgentRole.User, userMessage));
        var tools = _catalog.GetVisibleTools(_context);
        var maxIterations = Math.Max(1, _settings.MaxToolIterations);

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            State = AgentRunState.Thinking;
            var request = new AgentCompletionRequest(
                _settings.Model, _systemPrompt, _history.ToList(), tools, MaxTokens, 0.2, AgentToolChoice.Auto);

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

            _history.Add(new AgentMessage(AgentRole.Assistant,
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
                    yield return new AgentEvent.ConfirmationRequired(call, decision.Reason);

                    bool? approved;
                    try { approved = await tcs.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { approved = null; }
                    _pending.TryRemove(call.Id, out _);
                    State = AgentRunState.Running;

                    if (approved is null) yield break;             // abgebrochen
                    if (approved == false)
                    {
                        yield return RecordResult(call, new AgentToolResult(call.Id, "Vom Benutzer abgelehnt.", true, decision));
                        continue;
                    }
                }

                if (_actionCount >= _context.Policy.MaxActionsPerSession)
                {
                    yield return RecordResult(call, new AgentToolResult(call.Id,
                        $"Aktions-Limit dieser Session erreicht ({_context.Policy.MaxActionsPerSession}).", true, decision));
                    continue;
                }

                var result = await _invoker.InvokeAsync(call, _context, ct);
                _actionCount++;
                yield return RecordResult(call, result);
            }
        }

        State = AgentRunState.Done;
        yield return new AgentEvent.TurnCompleted(AgentStopReason.Length, new AgentUsage(0, 0));
    }

    private AgentEvent RecordResult(AgentToolCall call, AgentToolResult result)
    {
        _history.Add(new AgentMessage(AgentRole.Tool, result.Content,
            ToolCallId: call.Id, IsError: result.IsError, ToolName: call.Name));
        return new AgentEvent.ToolExecuted(result);
    }

    private GuardrailDecision Evaluate(AgentToolCall call)
    {
        if (!_registry.Tools.TryGetValue(call.Name, out var entry))
            return new GuardrailDecision(GuardrailVerdict.Allow,
                "Unbekanntes Tool — der Invoker meldet den Fehler.", Array.Empty<string>());

        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);
        return _guardrails.Evaluate(new GuardrailRequest(entry.Name, entry.RequiredLevel, args, _context));
    }
}
