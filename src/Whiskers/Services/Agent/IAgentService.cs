using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent;

/// <summary>Entry point from the UI or the instruct_agent MCP tool. Creates/loads a session
/// and drives the agentic loop (provider → gate+invoker → results back → until Stop).</summary>
public interface IAgentService
{
    Task<IAgentSession> StartSessionAsync(
        AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null, CancellationToken ct = default);
    Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>A running conversation. Events stream to the UI (Blazor).</summary>
public interface IAgentSession
{
    string SessionId { get; }
    AgentRunState State { get; }

    /// <summary>Snapshot of the conversation so far (for persistence).</summary>
    IReadOnlyList<AgentMessage> History { get; }

    /// <summary>Send a user turn; stream of events until the turn ends or a confirmation
    /// is required (hybrid mode). On AwaitingConfirmation the loop pauses.</summary>
    IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage, string? imageBase64 = null, string? imageMediaType = null,
        CancellationToken ct = default);

    /// <summary>Hybrid flow: approve or reject the open confirm tool call with this operation id
    /// (<see cref="Whiskers.Models.Agent.AgentToolCall.CorrelationId"/>, unique per call — never the provider's
    /// tool-call id, which can repeat). On approved=true the loop continues, otherwise the call is fed back as Deny.
    /// An id the session does not wait on (an ended run, a stale decision) changes nothing.</summary>
    Task ResolveConfirmationAsync(string operationId, bool approved, CancellationToken ct = default);
}
