using System.Collections.Concurrent;
using ServerWatch.Models.Cve;

namespace ServerWatch.Services.Cve;

/// <summary>
/// In-memory store of the latest CVE scan results per target.
/// Mirrors the style of <c>ImageUpdateStore</c> — no persistence; results are rebuilt each scan cycle.
/// Key format: "<serverId>:<containerId|os>".
/// </summary>
public class CveFindingsStore
{
    private readonly ConcurrentDictionary<string, CveScanResult> _results = new();

    public DateTime? LastScanAt { get; set; }
    public bool IsScanning { get; set; }

    private static string Key(string serverId, string? containerId)
        => $"{serverId}:{containerId ?? "os"}";

    public void Set(CveScanResult result)
        => _results[Key(result.ServerId, result.ContainerId)] = result;

    public CveScanResult? Get(string serverId, string? containerId)
        => _results.TryGetValue(Key(serverId, containerId), out var r) ? r : null;

    public IReadOnlyList<CveScanResult> GetForServer(string serverId)
        => _results.Values.Where(r => r.ServerId == serverId).ToList();

    public IReadOnlyList<CveScanResult> GetAll()
        => _results.Values.ToList();

    public void Remove(string serverId, string? containerId)
        => _results.TryRemove(Key(serverId, containerId), out _);

    public void Clear() => _results.Clear();

    /// <summary>Aggregate counts for one scan result (per target).</summary>
    public static CveSummary Summarize(CveScanResult result)
    {
        var s = new CveSummary
        {
            ServerId = result.ServerId,
            Source = result.Source,
            ContainerId = result.ContainerId,
            ContainerName = result.ContainerName,
            TotalCount = result.Findings.Count,
            LastScannedAt = result.ScannedAt,
            Error = result.Error
        };
        foreach (var f in result.Findings)
        {
            switch (f.Severity)
            {
                case CveSeverity.Critical: s.CriticalCount++; break;
                case CveSeverity.High: s.HighCount++; break;
                case CveSeverity.Medium: s.MediumCount++; break;
                case CveSeverity.Low: s.LowCount++; break;
            }
        }
        return s;
    }

    /// <summary>Aggregate one combined summary per server (OS + all containers merged).</summary>
    public CveSummary SummarizeServer(string serverId)
    {
        var s = new CveSummary { ServerId = serverId, Source = CveSource.Os };
        foreach (var r in GetForServer(serverId))
        {
            s.TotalCount += r.Findings.Count;
            foreach (var f in r.Findings)
            {
                switch (f.Severity)
                {
                    case CveSeverity.Critical: s.CriticalCount++; break;
                    case CveSeverity.High: s.HighCount++; break;
                    case CveSeverity.Medium: s.MediumCount++; break;
                    case CveSeverity.Low: s.LowCount++; break;
                }
            }
            if (r.ScannedAt > s.LastScannedAt) s.LastScannedAt = r.ScannedAt;
        }
        return s;
    }
}
