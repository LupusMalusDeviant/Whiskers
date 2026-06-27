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
    Task SaveAsync();
    CveSummary SummarizeServer(string serverId);

    /// <summary>De-duplicates every finding into one <see cref="CveGroup"/> per CVE-ID, listing all the
    /// real affected (server, container/OS, package) instances behind it. <paramref name="firstSeen"/>
    /// maps a finding's IdentityKey to when it was first detected (for the age indicator);
    /// <paramref name="serverNames"/> maps server id → display name.</summary>
    IReadOnlyList<CveGroup> BuildGroups(
        IReadOnlyDictionary<string, DateTime> firstSeen,
        IReadOnlyDictionary<string, string> serverNames);
}
