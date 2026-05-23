namespace ServerWatch.Models.Cve;

/// <summary>A single known vulnerability detected in either an OS package or a container image.</summary>
public class CveFinding
{
    public string ServerId { get; set; } = "local";
    public CveSource Source { get; set; }

    // Container-only — null for OS findings
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? Image { get; set; }

    public string CveId { get; set; } = string.Empty;
    public CveSeverity Severity { get; set; }
    public string Package { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? FixedVersion { get; set; }
    public string? Title { get; set; }
    public string? Reference { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Stable identity key for diffing (same vuln in same place across scans).</summary>
    public string IdentityKey =>
        $"{ServerId}|{Source}|{ContainerId ?? "-"}|{Package}|{CveId}";
}
