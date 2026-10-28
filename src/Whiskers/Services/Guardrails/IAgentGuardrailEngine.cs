using System.Text.Json;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Guardrails;

/// <summary>Evaluates a planned tool call BEFORE execution. Fully LLM-independent.
/// Aggregates all IGuardrailRule with "most-restrictive wins": a Deny beats everything,
/// otherwise a Confirm wins, otherwise Allow. Called by the IAgentToolInvoker (agent calls) AND the
/// DirectMcpCallGuard (direct MCP tool calls) — together they cover every path to a tool's side effects.</summary>
public interface IAgentGuardrailEngine
{
    GuardrailDecision Evaluate(GuardrailRequest request);
}

/// <summary>A single, testable rule. Built-in impls (data-driven from GuardrailPolicy):
/// PrincipalCeilingRule, MaxLevelRule, ToolAllowListRule, ToolDenyListRule, ProtectedResourceRule,
/// ForbiddenArgumentRule, ReadOnlyModeRule, RateLimitRule.</summary>
public interface IGuardrailRule
{
    string Id { get; }
    GuardrailVerdict Evaluate(GuardrailRequest request, out string reason);
}

public sealed record GuardrailRequest(
    string ToolName,
    string RequiredLevel,                                       // McpPermissionLevels.DefaultToolLevels[tool]
    IReadOnlyDictionary<string, JsonElement> Arguments,
    AgentContext Context);

public sealed record GuardrailDecision(
    GuardrailVerdict Verdict,
    string Reason,
    IReadOnlyList<string> MatchedRuleIds)
{
    /// <summary>The recorded gateway decision this verdict comes from (INV-22, R1-12), when one was recorded.</summary>
    public string? DecisionId { get; init; }

    /// <summary>The preset that decision was made under (a pinned run's own, or the active one).</summary>
    public string? Preset { get; init; }
}

public enum GuardrailVerdict { Allow, Confirm, Deny }
