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

    /// <summary>First-run config: a single restrictive "Standard" preset.</summary>
    public static GuardrailConfig SafeDefault() => new()
    {
        ActivePreset = "Standard",
        Presets = new() { new GuardrailPreset { Name = "Standard", Policy = GuardrailPolicy.SafeDefault() } },
    };
}
