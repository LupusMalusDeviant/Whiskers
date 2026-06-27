using System.Text.Json;

namespace ServerWatch.Models.Agent;

// The agent's provider-neutral language. Each IAgentLlmProvider impl translates these types
// into its wire format (OpenAI / Gemini / Anthropic) and back — the idiosyncrasies
// never leave the impl.

public enum AgentRole { System, User, Assistant, Tool }

public enum AgentToolChoice { Auto, None, Required }

public enum AgentStopReason { Stop, ToolCalls, Length, Filtered, Error }

public sealed record AgentUsage(int InputTokens, int OutputTokens);

public sealed record AgentToolDefinition(string Name, string Description, JsonElement JsonSchema);

public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);

public sealed record AgentMessage(
    AgentRole Role,
    string? Text = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,   // assistant requests tools
    string? ToolCallId = null,                         // tool response references a call (OpenAI/Anthropic)
    bool IsError = false,
    string? ToolName = null);                          // tool response: function name (Gemini pairs by name)

public sealed record AgentCompletionRequest(
    string Model,
    string? System,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    int MaxTokens = 1024,
    double Temperature = 0.2,
    AgentToolChoice ToolChoice = AgentToolChoice.Auto,
    string? Endpoint = null);

public sealed record AgentCompletion(
    string? AssistantText,
    IReadOnlyList<AgentToolCall> ToolCalls,
    AgentStopReason StopReason,
    AgentUsage Usage);

/// <summary>A streaming increment: either text, a (partial) tool call, or the turn end.</summary>
public sealed record AgentStreamDelta(
    string? TextDelta = null,
    AgentToolCall? ToolCallDelta = null,
    AgentStopReason? Final = null);
