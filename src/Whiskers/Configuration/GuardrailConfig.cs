namespace Whiskers.Configuration;

/// <summary>Why a <see cref="GuardrailConfig"/> cannot be enforced (see <see cref="GuardrailConfig.Problem"/>).</summary>
public enum GuardrailConfigProblemKind
{
    NoPresets,
    PresetWithoutName,
    DuplicatePresetName,
    PresetWithoutPolicy,
    MissingValue,
    ActivePresetMissing,
    InvalidPattern,
}

/// <summary>One reason a configuration is refused. <paramref name="Preset"/> names the preset it concerns,
/// <paramref name="Value"/> the field or pattern. The UI localizes it by <paramref name="Kind"/>; <see cref="ToString"/>
/// is the English text for logs.</summary>
public sealed record GuardrailConfigProblem(GuardrailConfigProblemKind Kind, string? Preset = null, string? Value = null)
{
    public override string ToString() => Kind switch
    {
        GuardrailConfigProblemKind.NoPresets => "At least one preset is required.",
        GuardrailConfigProblemKind.PresetWithoutName => "Every preset needs a name.",
        GuardrailConfigProblemKind.DuplicatePresetName => $"Preset names must be unique ('{Preset}').",
        GuardrailConfigProblemKind.PresetWithoutPolicy => $"Preset '{Preset}' has no policy.",
        GuardrailConfigProblemKind.MissingValue => $"Preset '{Preset}': '{Value}' is missing or holds an empty entry.",
        GuardrailConfigProblemKind.ActivePresetMissing => $"The active preset '{Preset}' does not exist.",
        GuardrailConfigProblemKind.InvalidPattern => $"Preset '{Preset}': '{Value}' is not a valid regex.",
        _ => Kind.ToString(),
    };
}

/// <summary>Thrown when a configuration that cannot be enforced is saved; nothing was changed.</summary>
public sealed class GuardrailConfigException(GuardrailConfigProblem problem) : InvalidOperationException(problem.ToString())
{
    public GuardrailConfigProblem Problem { get; } = problem;
}

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

    /// <summary>The policy of the active preset. An active preset that does not exist never falls back to another
    /// preset (R1-12): the result is the fail-closed policy. The store refuses such a configuration anyway.</summary>
    public GuardrailPolicy ActivePolicy()
    {
        var preset = Presets?.FirstOrDefault(p => p?.Name == ActivePreset);
        return preset?.Policy ?? GuardrailPolicy.FailClosed();
    }

    /// <summary>A deep copy: callers that edit it never change the live configuration. Missing parts stay missing,
    /// so <see cref="Problem"/> still sees them.</summary>
    public GuardrailConfig Clone() => new()
    {
        ActivePreset = ActivePreset,
        Presets = Presets?.Select(p => p is null ? null! : new GuardrailPreset { Name = p.Name, Policy = p.Policy?.Clone()! })
            .ToList()!,
    };

    /// <summary>Why this configuration cannot be enforced, or <c>null</c> when it can: no presets, an empty or
    /// duplicate preset name, a preset without a policy, a policy with a missing value or list (or a missing list
    /// entry), an active preset that does not exist, or a forbidden-argument pattern that is not a valid regex.</summary>
    public GuardrailConfigProblem? Problem()
    {
        if (Presets is null || Presets.Count == 0) return new(GuardrailConfigProblemKind.NoPresets);
        if (Presets.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name))) return new(GuardrailConfigProblemKind.PresetWithoutName);
        if (Presets.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            return new(GuardrailConfigProblemKind.DuplicatePresetName, duplicate.Key);
        foreach (var preset in Presets)
        {
            var policy = preset.Policy;
            if (policy is null) return new(GuardrailConfigProblemKind.PresetWithoutPolicy, preset.Name);
            if (policy.MaxAutonomousLevel is null) return new(GuardrailConfigProblemKind.MissingValue, preset.Name, nameof(policy.MaxAutonomousLevel));
            if (policy.ToolMode is null) return new(GuardrailConfigProblemKind.MissingValue, preset.Name, nameof(policy.ToolMode));
            foreach (var (name, list) in new[]
                     {
                         (nameof(policy.ToolDenyList), policy.ToolDenyList),
                         (nameof(policy.ToolAllowList), policy.ToolAllowList),
                         (nameof(policy.ProtectedResources), policy.ProtectedResources),
                         (nameof(policy.ForbiddenArgPatterns), policy.ForbiddenArgPatterns),
                     })
            {
                if (list is null || list.Any(entry => entry is null))
                    return new(GuardrailConfigProblemKind.MissingValue, preset.Name, name);
            }
        }
        if (!Presets.Any(p => p.Name == ActivePreset)) return new(GuardrailConfigProblemKind.ActivePresetMissing, ActivePreset);
        foreach (var preset in Presets)
        {
            foreach (var pattern in preset.Policy.ForbiddenArgPatterns)
            {
                try { _ = new System.Text.RegularExpressions.Regex(pattern); }
                catch (ArgumentException) { return new(GuardrailConfigProblemKind.InvalidPattern, preset.Name, pattern); }
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
