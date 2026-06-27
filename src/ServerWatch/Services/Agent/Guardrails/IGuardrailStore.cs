using System.Text.Json;
using ServerWatch.Configuration;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Guardrails;

/// <summary>Persists the guardrail policy in its OWN file (guardrails.json), separate from the
/// agent settings path. Writing is admin-only — a compromised settings editor cannot
/// take the guardrails with it. Manageable via UI; the engine loads exclusively from here.</summary>
public interface IGuardrailStore
{
    /// <summary>The active preset's policy — what the engine enforces.</summary>
    GuardrailPolicy Current { get; }

    /// <summary>All presets + which one is active (for the editor UI).</summary>
    GuardrailConfig Config { get; }

    /// <summary>Saves the whole preset config (admin-only). Throws if editor.PermissionLevel != Admin.</summary>
    Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default);

    /// <summary>Back-compat: replace the active preset's policy. Throws if editor isn't Admin.</summary>
    Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default);

    /// <summary>Fires after a successful save so consumers re-read the active policy.</summary>
    event Action? Changed;
}

/// <summary>Describes the available rule types for the UI — makes the guardrails extensible,
/// without the settings form having to hard-know every rule type.</summary>
public interface IGuardrailRuleCatalog
{
    IReadOnlyList<GuardrailRuleDescriptor> AvailableRules { get; }
}

public sealed record GuardrailRuleDescriptor(
    string Id,
    string DisplayName,
    string Description,
    GuardrailVerdict DefaultVerdict,
    JsonElement ConfigSchema);     // drives the settings form for this rule
