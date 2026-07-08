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

    /// <summary>Trivy image reference used for container scans.</summary>
    public string TrivyImage { get; set; } = "aquasec/trivy:latest";

    /// <summary>Send Mattermost notifications for new findings.</summary>
    public bool NotifyOnFinding { get; set; } = true;

    /// <summary>Minimum severity that triggers a notification: Low | Medium | High | Critical.</summary>
    public string NotifySeverity { get; set; } = "High";

    /// <summary>Maximum number of concurrent container scans per check cycle.</summary>
    public int MaxConcurrentScans { get; set; } = 3;

    /// <summary>Per-package timeout when resolving CVE-IDs via apt changelog.</summary>
    public int OsChangelogTimeoutSeconds { get; set; } = 15;
}
