namespace ServerWatch.Models.Cve;

/// <summary>One concrete place a CVE was actually detected — a single (server, container/OS, package)
/// instance. Only real scanner hits become an affected entry; nothing is inferred.</summary>
public sealed class CveAffected
{
    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public CveSource Source { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public string? Image { get; init; }
    public string? Os { get; init; }
    public string Package { get; init; } = "";
    public string? InstalledVersion { get; init; }
    public string? FixedVersion { get; init; }
    public bool Verified { get; init; }
    public bool HasFix { get; init; }
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>Human label of the target: container name or "OS".</summary>
    public string TargetLabel => Source == CveSource.Os ? "OS" : (ContainerName ?? ContainerId ?? "?");
}

/// <summary>A single CVE de-duplicated across every server/container it affects.
/// One row per CVE-ID, with all real affected instances listed behind it.</summary>
public sealed class CveGroup
{
    public required string CveId { get; init; }
    public CveSeverity Severity { get; set; }              // worst severity across instances
    public string? Title { get; set; }
    public string? Reference { get; set; }
    public DateTime? PublishedDate { get; set; }           // when the CVE was published (world age)
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow; // earliest detection in OUR environment
    public List<CveAffected> Affected { get; } = new();

    public bool IsVerified => !CveId.StartsWith("SECURITY-UPDATE/", StringComparison.Ordinal);
    public bool HasFix => Affected.Any(a => a.HasFix);
    public int InstanceCount => Affected.Count;
    public int ServerCount => Affected.Select(a => a.ServerId).Distinct().Count();
    public int ContainerCount => Affected.Where(a => a.Source == CveSource.Container)
        .Select(a => a.ContainerId).Distinct().Count();

    /// <summary>How long this CVE has been open in our environment.</summary>
    public TimeSpan OpenFor => DateTime.UtcNow - FirstSeenUtc;
}
