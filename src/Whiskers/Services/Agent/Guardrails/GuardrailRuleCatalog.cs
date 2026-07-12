using System.Text.Json;

namespace Whiskers.Services.Agent.Guardrails;

/// <summary>Describes the built-in rule types for the UI, without the settings form having to
/// hard-know every rule type. Extensible: new rules only add a descriptor here.</summary>
public sealed class GuardrailRuleCatalog : IGuardrailRuleCatalog
{
    private static readonly JsonElement EmptySchema =
        JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

    public IReadOnlyList<GuardrailRuleDescriptor> AvailableRules { get; } = new[]
    {
        new GuardrailRuleDescriptor("principal-ceiling", "Principal ceiling",
            "The agent can never exceed the rights of the triggering user/MCP key.",
            GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("read-only-mode", "Read-only kill switch",
            "Blocks every action above 'read'.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("tool-deny-list", "Tool deny list",
            "Named tools are always forbidden.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("tool-allow-list", "Tool allow list",
            "When set, only the listed tools are allowed.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("protected-resource", "Protected resources",
            "Glob patterns for containers/servers/DBs the agent must never touch.",
            GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("forbidden-argument", "Forbidden argument patterns",
            "Regexes against destructive shell/SQL arguments.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("confirmation", "Confirmation required",
            "Actions above the autonomous level require a UI confirmation.",
            GuardrailVerdict.Confirm, EmptySchema),
    };
}
