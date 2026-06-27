using System.Text.Json;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Guardrails;

/// <summary>Evaluates a planned tool call BEFORE execution. Fully LLM-independent.
/// Aggregates all IGuardrailRule with "most-restrictive wins": a Deny beats everything,
/// otherwise a Confirm wins, otherwise Allow. Called by both the IAgentToolInvoker AND McpPermissionCheck
/// — this is the only path to side effects and therefore inescapable.</summary>
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
    IReadOnlyList<string> MatchedRuleIds);

public enum GuardrailVerdict { Allow, Confirm, Deny }
