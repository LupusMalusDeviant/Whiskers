using System.Text.Json;

namespace ServerWatch.Services.Agent.Guardrails;

/// <summary>Describes the built-in rule types for the UI, without the settings form having to
/// hard-know every rule type. Extensible: new rules only add a descriptor here.</summary>
public sealed class GuardrailRuleCatalog : IGuardrailRuleCatalog
{
    private static readonly JsonElement EmptySchema =
        JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

    public IReadOnlyList<GuardrailRuleDescriptor> AvailableRules { get; } = new[]
    {
        new GuardrailRuleDescriptor("principal-ceiling", "Auslöser-Decke",
            "Der Agent kann nie über die Rechte des auslösenden Users/MCP-Keys hinaus.",
            GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("read-only-mode", "Read-Only-Kill-Switch",
            "Blockiert jede Aktion oberhalb von 'read'.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("tool-deny-list", "Tool-Deny-Liste",
            "Benannte Tools sind immer verboten.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("tool-allow-list", "Tool-Allow-Liste",
            "Wenn gesetzt, sind nur die gelisteten Tools erlaubt.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("protected-resource", "Geschützte Ressourcen",
            "Glob-Muster für Container/Server/DB, die der Agent nie anfassen darf.",
            GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("forbidden-argument", "Verbotene Argument-Muster",
            "Regex gegen destruktive Shell-/SQL-Argumente.", GuardrailVerdict.Deny, EmptySchema),
        new GuardrailRuleDescriptor("confirmation", "Bestätigungs-Pflicht",
            "Aktionen oberhalb des autonomen Levels erfordern eine UI-Bestätigung.",
            GuardrailVerdict.Confirm, EmptySchema),
    };
}
