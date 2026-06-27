using System.Text.Json.Serialization;

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

    /// <summary>OS the finding actually applies to — the IMAGE OS for container findings (from Trivy
    /// metadata, e.g. "debian 12") or the HOST OS for OS findings. Used to confirm the CVE really
    /// pertains to this target and to show the OS context in the UI.</summary>
    public string? OsContext { get; set; }

    /// <summary>When the vulnerability was published (from the scanner's vuln DB), if known.</summary>
    public DateTime? PublishedDate { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>A real CVE was matched against an actually-installed package+version (Trivy, or apt with a
    /// resolved CVE-ID) — i.e. confirmed applicable. False for synthetic "SECURITY-UPDATE/&lt;pkg&gt;"
    /// pending-update markers that have no CVE-ID.</summary>
    [JsonIgnore]
    public bool IsVerified => !CveId.StartsWith("SECURITY-UPDATE/", StringComparison.Ordinal);

    /// <summary>A fix is available (an upgrade target version is known).</summary>
    [JsonIgnore]
    public bool HasFix => !string.IsNullOrWhiteSpace(FixedVersion);

    /// <summary>Stable identity key for diffing (same vuln in same place across scans).</summary>
    [JsonIgnore]
    public string IdentityKey =>
        $"{ServerId}|{Source}|{ContainerId ?? "-"}|{Package}|{CveId}";
}
