using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Guardrails;

/// <summary>Persists the guardrail policy in its OWN file (guardrails.json), separate from the
/// agent settings path. Writing is admin-only — a compromised settings editor cannot
/// take the guardrails with it. Manageable via UI; the engine loads exclusively from here.</summary>
public interface IGuardrailStore
{
    /// <summary>The active preset's policy — what the engine enforces.</summary>
    GuardrailPolicy Current { get; }

    /// <summary>All presets + which one is active (for the editor UI). A copy; edits change nothing.</summary>
    GuardrailConfig Config { get; }

    /// <summary>Why the stored configuration is not enforced and the fail-closed policy is, or <c>null</c>.</summary>
    string? LoadError => null;

    /// <summary>The active preset's name and policy, read together (R1-12): a decision records the preset that
    /// actually produced it. The default is for test doubles; the store reads both from one state.</summary>
    (string Preset, GuardrailPolicy Policy) Active => (Config.ActivePreset, Current);

    /// <summary>Saves the whole preset config (admin-only). Throws if editor.PermissionLevel != Admin, and
    /// <see cref="GuardrailConfigException"/> for a configuration that cannot be enforced.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("guardrails.save")]
    Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default);

    /// <summary>Back-compat: replace the active preset's policy. Throws if editor isn't Admin.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("guardrails.save")]
    Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default);

    /// <summary>Switches the kill switch off (R1-11): <c>ReadOnlyMode</c> of the active preset becomes <c>false</c>,
    /// nothing else changes. The only configuration write the kill switch lets through. Admin-only.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("guardrails.kill_switch_off")]
    Task DisableKillSwitchAsync(AgentPrincipal editor, CancellationToken ct = default) =>
        throw new NotSupportedException("This guardrail store cannot switch the kill switch off.");

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
