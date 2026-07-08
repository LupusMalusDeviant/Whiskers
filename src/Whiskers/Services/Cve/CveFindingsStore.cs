using System.Collections.Concurrent;
using System.Text.Json;
using Whiskers.Models.Cve;

namespace Whiskers.Services.Cve;

/// <summary>
/// Store of the latest CVE scan results per target. Persisted to disk so results (and the last-scan
/// time) survive restarts — that way the app does NOT re-scan on every startup; it only scans on the
/// configured interval or a manual trigger, and only genuinely new findings notify.
/// Key format: "<serverId>:<containerId|os>".
/// </summary>
public class CveFindingsStore : ICveFindingsStore
{
    private readonly ConcurrentDictionary<string, CveScanResult> _results = new();
    private readonly ILogger<CveFindingsStore>? _logger;
    private readonly string _persistPath;

    public DateTime? LastScanAt { get; set; }
    public bool IsScanning { get; set; }

    private sealed class PersistModel
    {
        public Dictionary<string, CveScanResult> Results { get; set; } = new();
        public DateTime? LastScanAt { get; set; }
    }

    public CveFindingsStore(ILogger<CveFindingsStore>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? "/app/data/cve-findings.json";
        try
        {
            if (File.Exists(_persistPath))
            {
                var model = JsonSerializer.Deserialize<PersistModel>(File.ReadAllText(_persistPath));
                if (model is not null)
                {
                    foreach (var kv in model.Results) _results[kv.Key] = kv.Value;
                    LastScanAt = model.LastScanAt;
                    _logger?.LogInformation("Loaded persisted CVE findings: {Targets} target(s), last scan {Last}",
                        _results.Count, LastScanAt);
                }
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to load persisted CVE findings"); }
    }

    /// <summary>Persists the current results + last-scan time so they survive a restart.</summary>
    public async Task SaveAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var model = new PersistModel { Results = new Dictionary<string, CveScanResult>(_results), LastScanAt = LastScanAt };
            await File.WriteAllTextAsync(_persistPath, JsonSerializer.Serialize(model));
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to persist CVE findings"); }
    }

    public static string Key(string serverId, string? containerId)
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

    /// <summary>Removes stored CONTAINER results of a server whose key is not in <paramref name="liveKeys"/>
    /// (a recreated/deleted container leaves a phantom entry otherwise). The OS target key is never pruned.
    /// Only call with an authoritative live set (a successful container listing). Returns the count removed.</summary>
    public int PruneServer(string serverId, IReadOnlySet<string> liveKeys)
    {
        var osKey = Key(serverId, null);
        var removed = 0;
        foreach (var kv in _results.ToArray())
        {
            if (kv.Value.ServerId != serverId) continue;
            if (kv.Key == osKey) continue;            // never prune the OS target
            if (liveKeys.Contains(kv.Key)) continue;  // still present
            if (_results.TryRemove(kv.Key, out _)) removed++;
        }
        return removed;
    }

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

    /// <summary>De-duplicate every finding into one group per CVE-ID, with all real affected instances.</summary>
    public IReadOnlyList<CveGroup> BuildGroups(
        IReadOnlyDictionary<string, DateTime> firstSeen,
        IReadOnlyDictionary<string, string> serverNames)
    {
        var groups = new Dictionary<string, CveGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in _results.Values)
        {
            foreach (var f in result.Findings)
            {
                if (string.IsNullOrEmpty(f.CveId)) continue;

                if (!groups.TryGetValue(f.CveId, out var g))
                {
                    g = new CveGroup { CveId = f.CveId, Severity = f.Severity, FirstSeenUtc = DateTime.UtcNow };
                    groups[f.CveId] = g;
                }

                // Worst severity + best available metadata win at the group level.
                if (f.Severity > g.Severity) g.Severity = f.Severity;
                if (string.IsNullOrEmpty(g.Title) && !string.IsNullOrEmpty(f.Title)) g.Title = f.Title;
                if (string.IsNullOrEmpty(g.Reference) && !string.IsNullOrEmpty(f.Reference)) g.Reference = f.Reference;
                if (g.PublishedDate is null && f.PublishedDate is not null) g.PublishedDate = f.PublishedDate;

                var seen = firstSeen.TryGetValue(f.IdentityKey, out var ts) ? ts : f.DetectedAt;
                if (seen < g.FirstSeenUtc) g.FirstSeenUtc = seen;

                g.Affected.Add(new CveAffected
                {
                    ServerId = f.ServerId,
                    ServerName = serverNames.TryGetValue(f.ServerId, out var sn) ? sn : f.ServerId,
                    Source = f.Source,
                    ContainerId = f.ContainerId,
                    ContainerName = f.ContainerName,
                    Image = f.Image,
                    Os = f.OsContext,
                    Package = f.Package,
                    InstalledVersion = f.InstalledVersion,
                    FixedVersion = f.FixedVersion,
                    Verified = f.IsVerified,
                    HasFix = f.HasFix,
                    FirstSeenUtc = seen,
                });
            }
        }

        // Worst severity first, then longest-open first, then CVE-ID.
        return groups.Values
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.FirstSeenUtc)
            .ThenBy(g => g.CveId, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
