namespace ServerWatch.Models.Cve;

/// <summary>Aggregated counts for one target (server-OS or a single container).</summary>
public class CveSummary
{
    public string ServerId { get; set; } = "local";
    public CveSource Source { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }

    public int TotalCount { get; set; }
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    public DateTime LastScannedAt { get; set; }
    public string? Error { get; set; }
}
