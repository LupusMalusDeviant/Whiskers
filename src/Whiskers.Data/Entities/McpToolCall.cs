namespace Whiskers.Models;

/// <summary>A single MCP/agent tool invocation, recorded for governance/observability.
/// Captures who called what, with which (secret-redacted) parameters, the guardrail verdict,
/// the outcome and the duration.</summary>
public class McpToolCallEntity
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Email of the web user, MCP key name, or "ai-trigger:…".</summary>
    public string Actor { get; set; } = "";
    /// <summary>web | mcp | agent | trigger.</summary>
    public string ActorType { get; set; } = "";

    public string ToolName { get; set; } = "";
    /// <summary>Required permission level of the tool: read | write | admin.</summary>
    public string Level { get; set; } = "read";

    /// <summary>The tool arguments as JSON, secrets redacted.</summary>
    public string? ParamsJson { get; set; }

    /// <summary>Guardrail verdict: allow | confirm | deny.</summary>
    public string Verdict { get; set; } = "allow";

    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public string? ResultSummary { get; set; }
    public string? ServerId { get; set; }
    public string? Error { get; set; }
}
