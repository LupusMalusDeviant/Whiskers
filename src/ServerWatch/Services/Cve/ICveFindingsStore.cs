using ServerWatch.Models.Cve;

namespace ServerWatch.Services.Cve;

/// <summary>In-memory store of the latest CVE scan results per server/container.</summary>
public interface ICveFindingsStore
{
    bool IsScanning { get; set; }
    DateTime? LastScanAt { get; set; }
    void Set(CveScanResult result);
    CveScanResult? Get(string serverId, string? containerId);
    IReadOnlyList<CveScanResult> GetForServer(string serverId);
    IReadOnlyList<CveScanResult> GetAll();
    void Remove(string serverId, string? containerId);
    void Clear();
    CveSummary SummarizeServer(string serverId);
}
