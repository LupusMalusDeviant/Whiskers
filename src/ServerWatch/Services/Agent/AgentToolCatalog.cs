using System.Text.Json;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent.Guardrails;

namespace ServerWatch.Services.Agent;

/// <summary>Returns the LLM function definitions that are visible to the principal at all, given role
/// AND guardrails. A tool that would already be hard-blocked without arguments (trigger ceiling,
/// deny/allow list, read-only) is never even shown — the model cannot request it.
/// Argument-dependent rules (protected resources, forbidden patterns) only take effect at call time.</summary>
public sealed class AgentToolCatalog : IAgentToolCatalog
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoArgs =
        new Dictionary<string, JsonElement>();

    private readonly IAgentToolRegistry _registry;
    private readonly IAgentGuardrailEngine _guardrails;

    public AgentToolCatalog(IAgentToolRegistry registry, IAgentGuardrailEngine guardrails)
    {
        _registry = registry;
        _guardrails = guardrails;
    }

    public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context)
    {
        var visible = new List<AgentToolDefinition>();
        foreach (var entry in _registry.Tools.Values)
        {
            var request = new GuardrailRequest(entry.Name, entry.RequiredLevel, NoArgs, context);
            if (_guardrails.Evaluate(request).Verdict != GuardrailVerdict.Deny)
                visible.Add(entry.Definition);
        }
        return visible;
    }
}
