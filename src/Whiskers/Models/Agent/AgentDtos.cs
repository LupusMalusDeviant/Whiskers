using System.Text.Json;

namespace Whiskers.Models.Agent;

// The agent's provider-neutral language. Each IAgentLlmProvider impl translates these types
// into its wire format (OpenAI / Gemini / Anthropic) and back — the idiosyncrasies
// never leave the impl.

public enum AgentRole { System, User, Assistant, Tool }

public enum AgentToolChoice { Auto, None, Required }

public enum AgentStopReason { Stop, ToolCalls, Length, Filtered, Error }

public sealed record AgentUsage(int InputTokens, int OutputTokens);

public sealed record AgentToolDefinition(string Name, string Description, JsonElement JsonSchema);

/// <summary>A tool call requested by the model. <paramref name="ProviderSignature"/> carries an
/// opaque per-provider token that MUST be replayed with the call on the next request — e.g. Gemini's
/// <c>thoughtSignature</c> for thinking models (omitting it is a hard 400). Null for providers that don't use one.</summary>
public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson, string? ProviderSignature = null)
{
    /// <summary>Stable, globally-unique correlation id assigned when the call is created (WP-05).
    /// Unlike <see cref="Id"/> (the provider's tool_call id, which is only unique within a turn and
    /// can collide across sessions), this ties the SAME call object as it flows through the guardrail
    /// decision, the approval, the execution and the recorded history/notification. Because the one
    /// call instance is passed to both the approval bridge and the invoker, all of them see the same id.</summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}

public sealed record AgentMessage(
    AgentRole Role,
    string? Text = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,   // assistant requests tools
    string? ToolCallId = null,                         // tool response references a call (OpenAI/Anthropic)
    bool IsError = false,
    string? ToolName = null,                            // tool response: function name (Gemini pairs by name)
    string? ImageBase64 = null,                        // optional base64 image on a user turn (page screenshot for vision)
    string? ImageMediaType = "image/png");             // media type for ImageBase64

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
