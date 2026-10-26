namespace Whiskers.Configuration;

public class CveMonitorSettings
{
    public const string SectionName = "CveMonitor";

    /// <summary>Master switch. Off by default — opt-in via settings UI.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often the background scan runs.</summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>Whether to scan container images (via Trivy).</summary>
    public bool ScanContainers { get; set; } = true;

    /// <summary>Whether to scan the host OS for pending security updates.</summary>
    public bool ScanOs { get; set; } = true;

    /// <summary>Resolve CVE-IDs per OS package (slower, parses changelogs).</summary>
    public bool EnableOsCveIds { get; set; } = true;

    /// <summary>Trivy image reference used for container scans. It runs with the Docker socket mounted on every
    /// managed host, so it is pinned by digest — a moved tag must never decide what runs as root across the fleet
    /// (audit TA-14). Keep in sync with <c>appsettings.json</c>, which normally supplies the value.</summary>
    public string TrivyImage { get; set; } = "aquasec/trivy:0.72.0@sha256:cffe3f5161a47a6823fbd23d985795b3ed72a4c806da4c4df16266c02accdd6f";

    /// <summary>Send Mattermost notifications for new findings.</summary>
    public bool NotifyOnFinding { get; set; } = true;

    /// <summary>Minimum severity that triggers a notification: Low | Medium | High | Critical.</summary>
    public string NotifySeverity { get; set; } = "High";

    /// <summary>Maximum number of concurrent container scans per check cycle.</summary>
    public int MaxConcurrentScans { get; set; } = 3;

    /// <summary>Per-package timeout when resolving CVE-IDs via apt changelog.</summary>
    public int OsChangelogTimeoutSeconds { get; set; } = 15;
}
