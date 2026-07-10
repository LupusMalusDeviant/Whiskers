using Whiskers.Models.Cve;

namespace Whiskers.Services.Cve;

/// <summary>Core no-op defaults for the CVE services, used when the <b>Cve</b> module is off. The findings
/// store and monitor are consumed by Core pages (<c>Dashboard</c> + <c>ContainerDetail</c> inject
/// <c>ICveFindingsStore</c>; <c>Settings</c> injects both), so they need defaults that resolve when the module
/// is disabled — the pages then simply show no CVE data. The age store has no Core consumer but is injected by
/// the (inline-gated) <c>/cves</c> page, so its no-op keeps that injection safe without a <c>*View</c> split.
/// The real services win by last-registration when the module is enabled. Soft-dependency-via-no-op-Core-
/// contract pattern (RoadToSAP §2.1). Grouped in one file as a cohesive set for one module.</summary>
public sealed class NoopCveFindingsStore : ICveFindingsStore
{
    public bool IsScanning { get; set; }
    public DateTime? LastScanAt { get; set; }
    public void Set(CveScanResult result) { }
    public CveScanResult? Get(string serverId, string? containerId) => null;
    public IReadOnlyList<CveScanResult> GetForServer(string serverId) => Array.Empty<CveScanResult>();
    public IReadOnlyList<CveScanResult> GetAll() => Array.Empty<CveScanResult>();
    public void Remove(string serverId, string? containerId) { }
    public int PruneServer(string serverId, IReadOnlySet<string> liveKeys) => 0;
    public void Clear() { }
    public Task SaveAsync() => Task.CompletedTask;
    public CveSummary SummarizeServer(string serverId) => new() { ServerId = serverId };
    public IReadOnlyList<CveGroup> BuildGroups(
        IReadOnlyDictionary<string, DateTime> firstSeen,
        IReadOnlyDictionary<string, string> serverNames) => Array.Empty<CveGroup>();
}

public sealed class NoopCveMonitorService : ICveMonitorService
{
    public Task RunOneCycleAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NoopCveAgeStore : ICveAgeStore
{
    public Task RecordSeenAsync(IEnumerable<(string IdentityKey, string CveId)> current, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyDictionary<string, DateTime>> GetFirstSeenAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, DateTime>>(new Dictionary<string, DateTime>());
    public Task PruneStaleAsync(IReadOnlySet<string> liveKeys, DateTime olderThanUtc, CancellationToken ct = default) => Task.CompletedTask;
}
