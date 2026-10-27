namespace Whiskers.Configuration;

/// <summary>A named guardrail profile — a display name plus a full <see cref="GuardrailPolicy"/>.</summary>
public class GuardrailPreset
{
    public string Name { get; set; } = "Preset";
    public GuardrailPolicy Policy { get; set; } = new();
}

/// <summary>The persisted guardrail configuration: several named presets and which one is active.
/// The engine always sees the active preset's policy (via GuardrailStore.Current); switching presets
/// changes what the agent may do without editing rules.</summary>
public class GuardrailConfig
{
    public string ActivePreset { get; set; } = "Standard";
    public List<GuardrailPreset> Presets { get; set; } = new();

    /// <summary>The policy of the active preset (falls back to the first preset, then a SafeDefault).</summary>
    public GuardrailPolicy ActivePolicy()
    {
        var preset = Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets.FirstOrDefault();
        return preset?.Policy ?? GuardrailPolicy.SafeDefault();
    }

    /// <summary>A deep copy: callers that edit it never change the live configuration.</summary>
    public GuardrailConfig Clone() => new()
    {
        ActivePreset = ActivePreset,
        Presets = Presets.Select(p => new GuardrailPreset { Name = p.Name, Policy = p.Policy?.Clone()! }).ToList(),
    };

    /// <summary>Why this configuration cannot be enforced, or <c>null</c> when it can: no presets, an empty or
    /// duplicate preset name, a preset without a policy, an active preset that does not exist, or a
    /// forbidden-argument pattern that is not a valid regex.</summary>
    public string? Problem()
    {
        if (Presets is null || Presets.Count == 0) return "At least one preset is required.";
        if (Presets.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name))) return "Every preset needs a name.";
        if (Presets.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)) return "Preset names must be unique.";
        if (Presets.FirstOrDefault(p => p.Policy is null) is { } empty) return $"Preset '{empty.Name}' has no policy.";
        if (!Presets.Any(p => p.Name == ActivePreset)) return $"The active preset '{ActivePreset}' does not exist.";
        foreach (var preset in Presets)
        {
            foreach (var pattern in preset.Policy.ForbiddenArgPatterns)
            {
                try { _ = new System.Text.RegularExpressions.Regex(pattern); }
                catch (ArgumentException) { return $"Preset '{preset.Name}': '{pattern}' is not a valid regex."; }
            }
        }
        return null;
    }

    /// <summary>First-run config: a single restrictive "Standard" preset.</summary>
    public static GuardrailConfig SafeDefault() => new()
    {
        ActivePreset = "Standard",
        Presets = new() { new GuardrailPreset { Name = "Standard", Policy = GuardrailPolicy.SafeDefault() } },
    };
}
