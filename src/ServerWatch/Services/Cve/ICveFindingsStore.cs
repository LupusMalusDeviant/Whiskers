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

    /// <summary>Removes stored container results of a server whose key is absent from <paramref name="liveKeys"/>
    /// (phantom entries left by recreated/deleted containers). The OS target key is never pruned. Only call
    /// with an authoritative live set (a successful container listing). Returns the count removed.</summary>
    int PruneServer(string serverId, IReadOnlySet<string> liveKeys);

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
