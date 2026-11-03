using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;
using Whiskers.Models;

namespace Whiskers.Services.Observability.SelfMetrics;

/// <summary>
/// Writes the self-metrics to disk once a minute and restores them on boot (Plan-0003 WP3.2/WP3.3).
///
/// <para>The history is the smaller half of the point. The larger half is the restore: after a restart the
/// in-memory view is empty, and an empty "last success" is indistinguishable from "never succeeded". A
/// supervisor facing that has only bad options — alarm on every restart, or stay silent about fresh loops,
/// which is exactly the window in which a bad deploy has most likely broken something.</para>
///
/// <para>Sampling is a database write per loop and server per minute and nothing else: no Docker call, no
/// inspection, no work on the hosts. A self-measurement that adds load to what it measures is the same
/// mistake it exists to reveal, one level up.</para>
/// </summary>
public sealed class SelfMetricsRecorder : Whiskers.Services.FleetBackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the samples are kept. Longer than the metric retention on purpose: the question
    /// these answer — "was this loop already struggling before the deploy?" — is usually asked weeks late.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>How far back a restored success may reach. Beyond this the reading says nothing useful about
    /// now, and pretending otherwise would let a loop that has been dead for a month look recently alive.</summary>
    public static readonly TimeSpan MaxRestoreAge = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISelfMetrics _metrics;
    private readonly ILogger<SelfMetricsRecorder> _logger;
    private DateTime _lastPrune = DateTime.MinValue;

    public SelfMetricsRecorder(
        IServiceScopeFactory scopeFactory, ISelfMetrics metrics, ILogger<SelfMetricsRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        // Restore first, before any sample is written: a sample taken from an empty in-memory view would
        // persist the very gap the restore exists to close.
        try
        {
            await RestoreAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // Losing the restore costs history and a few false-quiet minutes; failing to start would cost
            // the self-measurement entirely. The loops keep running either way.
            _logger.LogWarning(ex, "Could not restore the self-metrics from the last run");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(SampleInterval, stoppingToken);

            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                _logger.LogWarning(ex, "Could not write the self-metrics sample");
            }
        }
    }

    /// <summary>Reads the newest sample per loop and server back into memory. Public for the test that proves
    /// a restart does not turn into a false alarm.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var cutoff = DateTime.UtcNow - MaxRestoreAge;

        var latest = await db.SelfMetricSamples
            .AsNoTracking()
            .Where(s => s.TakenAtUtc >= cutoff)
            .GroupBy(s => new { s.Loop, s.ServerId })
            .Select(g => g.OrderByDescending(s => s.TakenAtUtc).First())
            .ToListAsync(ct);

        foreach (var sample in latest)
        {
            _metrics.Restore(
                sample.Loop,
                sample.ServerId,
                sample.LastSuccessUtc,
                sample.ExpectedIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        }

        if (latest.Count > 0)
            _logger.LogInformation("Restored the self-metrics for {Count} loop/server pair(s) from the last run", latest.Count);
    }

    /// <summary>Writes one reading per loop and server. Public so a test can drive it.</summary>
    public async Task SampleAsync(CancellationToken ct = default)
    {
        var loops = _metrics.Loops();
        if (loops.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var now = DateTime.UtcNow;

        foreach (var loop in loops)
        {
            db.SelfMetricSamples.Add(new SelfMetricSampleEntity
            {
                TakenAtUtc = now,
                Loop = loop.Loop,
                ServerId = loop.ServerId,
                LastSuccessUtc = loop.LastSuccess,
                LastDurationMs = loop.LastDuration.TotalMilliseconds,
                Cycles = loop.Cycles,
                Failures = loop.Failures,
                Skips = loop.Skips,
                SkipReason = loop.SkipReason,
                ExpectedIntervalSeconds = loop.ExpectedInterval?.TotalSeconds
            });
        }

        await db.SaveChangesAsync(ct);

        // WP3.3 — its own retention, pruned here rather than bolted onto the metrics collector, so that a
        // change to metric retention cannot silently shorten the record of Whiskers' own behaviour.
        if (now - _lastPrune > TimeSpan.FromHours(1))
        {
            _lastPrune = now;
            var cutoff = now - Retention;
            await db.SelfMetricSamples.Where(s => s.TakenAtUtc < cutoff).ExecuteDeleteAsync(ct);
        }
    }
}
