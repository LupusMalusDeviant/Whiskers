namespace Whiskers.Configuration;

/// <summary>The agent's provider access. Deliberately WITHOUT guardrails — those live in a separate,
/// admin-only writable guardrails.json (see IGuardrailStore / GuardrailPolicy).</summary>
public class AgentSettings
{
    public const string SectionName = "Agent";

    public bool Enabled { get; set; }

    /// <summary>openai | openrouter | ollama | gemini | anthropic | claude-code</summary>
    public string Provider { get; set; } = "openai";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Custom endpoint for Ollama / OpenRouter / self-hosting. null = provider default.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Reference/value of the API key. Lives in the VaultService, not in plaintext JSON.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Hard upper bound on tool iterations per turn — code-enforced, not in the prompt.</summary>
    public int MaxToolIterations { get; set; } = 8;

    /// <summary>Max output tokens per model turn. The old hard-coded 1024 truncated tool-call JSON.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Custom system prompt for the agent. Empty = use the built-in default
    /// (AgentService.SystemPrompt). UI-editable; does NOT relax the guardrails (those are enforced
    /// at the tool boundary regardless of the prompt).</summary>
    public string SystemPrompt { get; set; } = "";
}
