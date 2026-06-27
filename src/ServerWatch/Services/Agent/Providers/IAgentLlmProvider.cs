using ServerWatch.Configuration;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Provider-agnostic LLM access with tool calling. One impl per wire format:
/// OpenAiCompatibleProvider (covers openai|openrouter|ollama), GeminiProvider, AnthropicProvider.
/// Claude Code is deliberately NOT a provider (its own loop) — see IClaudeCodeRuntime.</summary>
public interface IAgentLlmProvider
{
    /// <summary>Stable key: "openai" | "openrouter" | "ollama" | "gemini" | "anthropic".</summary>
    string Id { get; }

    /// <summary>A single model turn as a stream. Text deltas and/or tool calls,
    /// completed by a delta with Final set. Non-streaming backends
    /// emulate this with a single delta.</summary>
    IAsyncEnumerable<AgentStreamDelta> StreamAsync(AgentCompletionRequest request, CancellationToken ct = default);

    /// <summary>Lists the model ids available to the configured key/endpoint. Used to verify the
    /// key and populate the model picker. Throws on an auth or network failure.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>Selects the matching provider impl based on the settings.</summary>
public interface IAgentProviderFactory
{
    IAgentLlmProvider Resolve(AgentSettings settings);
    IReadOnlyCollection<string> SupportedProviderIds { get; }
}
