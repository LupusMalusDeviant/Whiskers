using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>
/// R1-55: rolls raw metric samples up into the fixed tiers (<see cref="MetricTiers"/>) and applies each tier's retention.
/// Run by the metrics collector after its samples are saved, so no sample can arrive later for a window it closed.
/// </summary>
public interface IMetricRollupService
{
    /// <summary>Rolls up every closed window not rolled up yet, a bounded amount per call; each batch of windows is one
    /// transaction together with its tier's watermark, so a crash neither loses nor double-counts a window.</summary>
    /// <returns>How many windows were written and whether more were due than one call takes (the upgrade's first pass).</returns>
    Task<MetricRollupResult> RollUpAsync(MetricsDbContext db, DateTime now, CancellationToken ct);

    /// <summary>Deletes what each tier's retention allows — never anything a coarser tier has not taken over yet — and
    /// the alert history after its own retention.</summary>
    Task<MetricPruneResult> PruneAsync(MetricsDbContext db, DateTime now, MetricRetention retention, CancellationToken ct);
}

public sealed record MetricRollupResult(int Windows, bool MoreDue);

/// <summary>Rows removed per table and tier (<c>container/raw</c>, <c>server/60</c>, <c>alerts</c>, …).</summary>
public sealed record MetricPruneResult(IReadOnlyDictionary<string, int> Removed);
