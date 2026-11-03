using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>
/// R1-55: the tiered roll-up and retention of the container and server metrics (Beszel's idea, Whiskers' own model).
/// <list type="bullet">
/// <item>Only closed windows are rolled up: the 10-minute tier up to the window holding <c>now</c>, every coarser tier
/// only up to its finer tier's watermark — a window is built from complete input or not at all.</item>
/// <item>Each batch of windows is one transaction: the batch's old rows (from a run interrupted after its commit was
/// unknown) are deleted, the new ones written and the tier's watermark moved — together or not at all. A run after a
/// crash starts at the watermark and rebuilds the same windows from the same input, so nothing is lost or counted
/// twice.</item>
/// <item>Retention deletes a tier's data only when it is past the tier's own retention <b>and</b> behind the next
/// coarser tier's watermark, so nothing is deleted before the windows built from it are committed — also when that
/// coarser roll-up keeps failing, and across a restart (the watermarks are on disk).</item>
/// </list>
/// </summary>
public sealed class MetricRollupService : IMetricRollupService
{
    /// <summary>At most this many batches per tier and call: the upgrade's first pass over days of raw data is spread
    /// over several collector cycles instead of holding one up.</summary>
    internal int MaxBatchesPerTier { get; init; } = 8;

    /// <summary>Tests only: runs after a batch is written, before its commit — throwing there is a crash mid-roll-up.</summary>
    internal Func<string, int, CancellationToken, Task>? BeforeCommit { get; set; }

    private static TimeSpan BatchSpan(int tierMinutes) => tierMinutes switch
    {
        10 => TimeSpan.FromHours(6),
        60 => TimeSpan.FromDays(2),
        _ => TimeSpan.FromDays(10),
    };

    public async Task<MetricRollupResult> RollUpAsync(MetricsDbContext db, DateTime now, CancellationToken ct)
    {
        var windows = 0;
        var more = false;
        ExceptionDispatchInfo? failure = null;
        foreach (var kind in MetricSeriesKind.All)
        {
            try
            {
                var source = 0;
                DateTime? sourceMark = now;   // raw data is complete up to now: the collector has saved this cycle's samples
                foreach (var tier in MetricTiers.Minutes)
                {
                    var size = MetricTiers.Size(tier);
                    DateTime? cut = sourceMark is { } m ? MetricTiers.Floor(m, size) : null;
                    var (written, mark, pending) = cut is { } c
                        ? await RollTierAsync(db, kind, source, tier, c, ct)
                        : (0, await MarkAsync(db, kind, tier, ct), false);
                    windows += written;
                    more |= pending;
                    sourceMark = mark;
                    source = tier;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The other kind still rolls up; the first failure is reported once both have run.
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        failure?.Throw();
        return new MetricRollupResult(windows, more);
    }

    private static Task<DateTime?> MarkAsync(MetricsDbContext db, MetricSeriesKind kind, int tier, CancellationToken ct)
    {
        var id = MetricTiers.StateId(kind.Name, tier);
        return db.MetricRollupStates.AsNoTracking().Where(s => s.Id == id)
            .Select(s => (DateTime?)s.RolledUpTo).FirstOrDefaultAsync(ct);
    }

    private async Task<(int Written, DateTime? Mark, bool Pending)> RollTierAsync(MetricsDbContext db, MetricSeriesKind kind,
        int source, int tier, DateTime cut, CancellationToken ct)
    {
        var size = MetricTiers.Size(tier);
        var mark = await MarkAsync(db, kind, tier, ct);
        DateTime start;
        if (mark is { } m) start = m;
        else
        {
            // First run (the upgrade): from the oldest data there is, so existing raw data is rolled up once.
            var first = await kind.FirstAsync(db, source, DateTime.MinValue, ct);
            if (first is null) return (0, null, false);
            start = MetricTiers.Floor(first.Value, size);
        }

        var written = 0;
        for (var batches = 0; start < cut; batches++)
        {
            if (batches == MaxBatchesPerTier) return (written, mark, true);
            ct.ThrowIfCancellationRequested();

            var next = await kind.FirstAsync(db, source, start, ct);
            DateTime end;
            List<WindowStats> rows;
            if (next is null || next.Value >= cut)
            {
                end = cut;   // nothing more to roll up: only the watermark moves
                rows = [];
            }
            else
            {
                if (next.Value >= start + size) start = MetricTiers.Floor(next.Value, size);   // skip an empty stretch
                end = start + BatchSpan(tier) < cut ? start + BatchSpan(tier) : cut;
                rows = await kind.AggregateAsync(db, source, start, end, size, ct);
            }

            await CommitAsync(db, kind, tier, start, end, rows, ct);
            written += rows.Count;
            start = end;
            mark = end;
        }
        return (written, mark, false);
    }

    private async Task CommitAsync(MetricsDbContext db, MetricSeriesKind kind, int tier, DateTime from, DateTime to,
        List<WindowStats> rows, CancellationToken ct)
    {
        var id = MetricTiers.StateId(kind.Name, tier);
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await kind.DeleteAsync(db, tier, from, to, ct);
                kind.Add(db, tier, rows);
                var state = await db.MetricRollupStates.FindAsync([id], ct);
                if (state is null) db.MetricRollupStates.Add(new MetricRollupStateEntity { Id = id, RolledUpTo = to });
                else state.RolledUpTo = to;
                await db.SaveChangesAsync(ct);
                if (BeforeCommit is { } hook) await hook(kind.Name, tier, ct);
                await tx.CommitAsync(ct);
            });
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<MetricPruneResult> PruneAsync(MetricsDbContext db, DateTime now, MetricRetention retention, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);
        var removed = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] levels = [0, .. MetricTiers.Minutes];
        foreach (var kind in MetricSeriesKind.All)
        {
            var prefix = kind.Name + "/";
            var marks = await db.MetricRollupStates.AsNoTracking().Where(s => s.Id.StartsWith(prefix))
                .ToDictionaryAsync(s => s.Id, s => s.RolledUpTo, ct);
            for (var i = 0; i < levels.Length; i++)
            {
                var tier = levels[i];
                var days = Math.Max(MetricRetention.MinDays, tier == 0 ? retention.RawDays : retention.ForTier(tier));
                var cutoff = now.AddDays(-days);
                if (i + 1 < levels.Length)
                {
                    // Not before the coarser tier has committed the windows built from it — no watermark, nothing.
                    var successor = marks.TryGetValue(MetricTiers.StateId(kind.Name, levels[i + 1]), out var mark)
                        ? mark : DateTime.MinValue;
                    if (successor < cutoff) cutoff = successor;
                }
                if (cutoff == DateTime.MinValue) continue;
                removed[$"{kind.Name}/{(tier == 0 ? "raw" : tier.ToString(System.Globalization.CultureInfo.InvariantCulture))}"] =
                    await kind.PruneAsync(db, tier, cutoff, ct);
            }
        }

        // The alert history has its own retention: a shorter raw retention does not shorten it.
        var alertCutoff = now.AddDays(-Math.Max(MetricRetention.MinDays, retention.AlertHistoryDays));
        removed["alerts"] = await db.AlertHistory.Where(a => a.Timestamp < alertCutoff).ExecuteDeleteAsync(ct);
        return new MetricPruneResult(removed);
    }
}
