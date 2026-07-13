using Whiskers.Configuration;
using Whiskers.Services.Agent.Guardrails;

namespace Whiskers.Models.Agent;

/// <summary>Who triggered the agent. The agent inherits this principal's rights and
/// can NEVER exceed them (PrincipalCeilingRule). Either a logged-in web user
/// or the MCP key of a triggering agent (e.g. external Claude Code).</summary>
public sealed record AgentPrincipal(
    AgentPrincipalKind Kind,
    string DisplayName,                       // email or key name (audit/UI)
    string PermissionLevel,                   // resolved: McpPermissionLevels.Read|Write|Admin
    IReadOnlyList<string>? AllowedTools,      // from McpApiKeyConfig; null = level defaults
    string? UserEmail = null,                 // set for WebUser
    string? McpKeyId = null);                 // set for McpKey

public enum AgentPrincipalKind { WebUser, McpKey }

/// <summary>Which surface the request came in through.</summary>
public enum AgentOrigin
{
    WebUi,        // human in the Whiskers UI
    McpTool,      // external agent calls the instruct_agent tool via /mcp
    Trigger       // autonomous run started by an AI trigger (no human)
}

/// <summary>Full execution context of a tool call — flows into the gate and the audit.</summary>
public sealed record AgentContext(
    string SessionId,
    AgentPrincipal Principal,
    AgentOrigin Origin,
    GuardrailPolicy Policy);

/// <summary>Result of exactly one tool call. A Deny is also a result (IsError=true),
/// fed back into the loop so the model "sees" the boundary.</summary>
public sealed record AgentToolResult(
    string ToolCallId,
    string Content,
    bool IsError,
    GuardrailDecision Decision);

public enum AgentRunState { Idle, Thinking, AwaitingConfirmation, Running, Done, Error }

/// <summary>UI-directed events — deliberately separate from the provider DTOs.</summary>
public abstract record AgentEvent
{
    public sealed record AssistantDelta(string Text) : AgentEvent;
    public sealed record ToolProposed(AgentToolCall Call, GuardrailDecision Decision) : AgentEvent;
    public sealed record ConfirmationRequired(AgentToolCall Call, string Reason, GuardrailDecision Decision) : AgentEvent;
    public sealed record ToolExecuted(AgentToolResult Result) : AgentEvent;
    public sealed record TurnCompleted(AgentStopReason Reason, AgentUsage Usage) : AgentEvent;
    public sealed record Failed(string Message) : AgentEvent;
}
