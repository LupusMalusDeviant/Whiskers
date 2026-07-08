using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent;

/// <summary>Orchestrates Claude Code (CLI / Agent SDK) as a subprocess. Deliberately NOT an IAgentLlmProvider,
/// because Claude Code brings its own agentic loop. It is configured with a dedicated
/// MCP agent key that points at our /mcp endpoint — so its tool calls also run through the same
/// guardrail gate. Externally it yields the same AgentEvents.</summary>
public interface IClaudeCodeRuntime
{
    /// <summary>Is the Claude Code CLI installed / the SDK reachable?</summary>
    bool IsAvailable { get; }

    IAsyncEnumerable<AgentEvent> RunAsync(AgentContext context, string userMessage, CancellationToken ct = default);
}
