namespace Whiskers.Models.Cve;

/// <summary>Result of one scan run, targeting either an OS or a single container.</summary>
public class CveScanResult
{
    public string ServerId { get; set; } = "local";
    public CveSource Source { get; set; }

    // Container-only — null for OS findings
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? Image { get; set; }

    public List<CveFinding> Findings { get; set; } = new();
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}
