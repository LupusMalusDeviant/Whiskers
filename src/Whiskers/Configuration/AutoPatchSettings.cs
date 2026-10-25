using Whiskers.Models.Cve;

namespace Whiskers.Configuration;

/// <summary>
/// Fleet-wide security auto-patching (section <c>"AutoPatch"</c> of app-settings.json, applied live).
///
/// <para>Rolls a container onto the newest image under the tag it already uses — never a new tag, never a
/// new major version — when that container carries fixable vulnerabilities. Off by default, and a dry run
/// when first switched on: the first night only reports what it would do.</para>
/// </summary>
public class AutoPatchSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Report what would be updated without recreating anything. Switch off only after one dry run
    /// has been read.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Hour of day (UTC) of the nightly maintenance window. One cycle per day, started inside it.</summary>
    public int RunAtHourUtc { get; set; } = 3;

    /// <summary>Lowest severity of a fixable finding that makes a container a candidate.</summary>
    public CveSeverity MinSeverity { get; set; } = CveSeverity.High;

    /// <summary>Upper bound per night, so one bad registry day cannot recreate the whole fleet at once.</summary>
    public int MaxPerCycle { get; set; } = 10;

    /// <summary>Container names never to touch, in addition to <c>AutoPatchPlanner.AlwaysExcluded</c>. An
    /// array, not a list: the configuration binder appends to a pre-filled list instead of replacing it.</summary>
    public string[] ExcludeContainers { get; set; } = [];
}
