using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Services.Metrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-55: raw samples roll up into fixed tiers (10 min / 1 h / 8 h) with average, maximum and sample count; only closed
/// windows; a roll-up interrupted before its commit neither loses nor double-counts a window; retention deletes nothing a
/// coarser tier has not committed — also when that roll-up fails and across a restart; a query covers its whole range
/// without gaps or duplicates, weighted by sample count, with true maxima, also over 30 days through the MCP tool.
/// </summary>
public sealed class MetricRollupTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sw-r155-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestSqlite.ClearPool(_path);
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private MetricsDbContext Db()
    {
        var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={_path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task RollUpAllAsync(MetricsDbContext db, DateTime now, MetricRollupService? service = null)
    {
        service ??= new MetricRollupService();
        for (var i = 0; i < 500; i++)
            if (!(await service.RollUpAsync(db, now, default)).MoreDue) return;
        Assert.Fail("the roll-up never caught up");
    }

    /// <summary>Container c1 on s1, one sample every 30 s from <paramref name="from"/> to <paramref name="to"/> inclusive.
    /// CPU 10 % except the given spikes; memory 50 % of its limit.</summary>
    private static async Task SeedContainerAsync(MetricsDbContext db, DateTime from, DateTime to, Func<DateTime, double>? cpu = null,
        Func<DateTime, bool>? skip = null, string container = "c1")
    {
        var rows = new List<ContainerMetricEntity>();
        for (var t = from; t <= to; t = t.AddSeconds(30))
        {
            if (skip?.Invoke(t) == true) continue;
            rows.Add(new ContainerMetricEntity
            {
                ContainerId = container, ContainerName = "web", ServerId = "s1", Timestamp = t,
                CpuPercent = cpu?.Invoke(t) ?? 10, MemoryUsageBytes = 50, MemoryLimitBytes = 100,
            });
        }
        db.ContainerMetrics.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static bool InSpike(DateTime t, DateTime spike) => t >= spike && t < spike.AddMinutes(2);

    // ---------------------------------------------------------------- roll-up

    [Fact]
    public async Task Raw_samples_roll_up_into_the_tiers_with_average_maximum_and_count_and_only_closed_windows()
    {
        var spike = T0.AddHours(3).AddMinutes(5);
        await using (var db = Db())
        {
            // An uneven window: 02:00–02:10 has only its first 5 samples.
            await SeedContainerAsync(db, T0, T0.AddHours(16), t => InSpike(t, spike) ? 90 : 10,
                skip: t => t >= T0.AddHours(2).AddMinutes(2.5) && t < T0.AddHours(2).AddMinutes(10));
            await RollUpAllAsync(db, T0.AddHours(16).AddMinutes(1));
        }

        await using var check = Db();
        var ten = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 10).ToListAsync();
        var spikeWindow = ten.Single(r => r.WindowStart == T0.AddHours(3));
        Assert.Equal(20, spikeWindow.SampleCount);
        Assert.Equal(90, spikeWindow.CpuMax);
        Assert.Equal((16 * 10 + 4 * 90) / 20.0, spikeWindow.CpuAvg, 9);
        Assert.Equal(50, spikeWindow.MemoryPercentAvg, 9);
        Assert.Equal("web", spikeWindow.ContainerName);
        Assert.Equal(5, ten.Single(r => r.WindowStart == T0.AddHours(2)).SampleCount);
        // The window holding "now" is open: its raw sample at 16:00 is not rolled up yet.
        Assert.DoesNotContain(ten, r => r.WindowStart == T0.AddHours(16));
        Assert.Equal(96, ten.Count);

        var hour = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 60).ToListAsync();
        var spikeHour = hour.Single(r => r.WindowStart == T0.AddHours(3));
        Assert.Equal((120, 90.0), (spikeHour.SampleCount, spikeHour.CpuMax));
        Assert.Equal((116 * 10 + 4 * 90) / 120.0, spikeHour.CpuAvg, 9);   // weighted by count, not a mean of window means
        Assert.Equal(120 - 15, hour.Single(r => r.WindowStart == T0.AddHours(2)).SampleCount);

        var eight = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 480).OrderBy(r => r.WindowStart).ToListAsync();
        Assert.Equal([T0, T0.AddHours(8)], eight.Select(r => r.WindowStart));
        Assert.Equal(90, eight[0].CpuMax);
        Assert.Equal(960 - 15, eight[0].SampleCount);
        Assert.Equal(960, eight[1].SampleCount);
        Assert.Equal(10, eight[1].CpuMax);
    }

    [Fact]
    public async Task Server_series_roll_up_too()
    {
        await using (var db = Db())
        {
            for (var t = T0; t < T0.AddMinutes(20); t = t.AddMinutes(1))
                db.ServerMetrics.Add(new ServerMetricEntity { ServerId = "s1", ServerName = "Badwolf", Timestamp = t,
                    CpuPercent = t == T0.AddMinutes(3) ? 95 : 5, MemoryUsedBytes = 25, MemoryTotalBytes = 100 });
            await db.SaveChangesAsync();
            await RollUpAllAsync(db, T0.AddMinutes(25));
        }

        await using var check = Db();
        var first = await check.ServerMetricRollups.SingleAsync(r => r.TierMinutes == 10 && r.WindowStart == T0);
        Assert.Equal((10, 95.0, 25.0, "Badwolf"), (first.SampleCount, first.CpuMax, first.MemoryPercentAvg, first.ServerName));
        Assert.Equal((9 * 5 + 95) / 10.0, first.CpuAvg, 9);
    }

    [Fact]
    public async Task A_crash_before_the_first_tier_commits_loses_and_doubles_nothing()
    {
        await using (var db = Db()) await SeedContainerAsync(db, T0, T0.AddHours(2));
        var crashing = new MetricRollupService { BeforeCommit = (_, tier, _) => tier == 10 ? throw new IOException("power loss") : Task.CompletedTask };

        await using (var db = Db())
            await Assert.ThrowsAsync<IOException>(() => crashing.RollUpAsync(db, T0.AddHours(2).AddMinutes(1), default));

        await using (var db = Db())
        {
            Assert.Empty(await db.ContainerMetricRollups.ToListAsync());
            Assert.Empty(await db.MetricRollupStates.Where(s => s.Id.StartsWith("container/")).ToListAsync());
            Assert.Equal(241, await db.ContainerMetrics.CountAsync());
            // Retention deletes no raw sample whose window is not committed, however short the raw retention.
            await new MetricRollupService().PruneAsync(db, T0.AddDays(5), MetricRetention.Default with { RawDays = 1 }, default);
            Assert.Equal(241, await db.ContainerMetrics.CountAsync());
        }

        // After the restart: rolled up exactly once — and again changes nothing.
        await using (var db = Db()) await RollUpAllAsync(db, T0.AddHours(2).AddMinutes(1));
        await using (var db = Db()) await RollUpAllAsync(db, T0.AddHours(2).AddMinutes(1));
        await using var check = Db();
        var ten = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 10).ToListAsync();
        Assert.Equal(12, ten.Count);
        Assert.Equal(240, ten.Sum(r => r.SampleCount));
        Assert.Equal(240, (await check.ContainerMetricRollups.Where(r => r.TierMinutes == 60).ToListAsync()).Sum(r => r.SampleCount));
    }

    [Fact]
    public async Task A_window_rolled_up_again_after_an_unknown_commit_is_replaced_not_added()
    {
        await using (var db = Db())
        {
            await SeedContainerAsync(db, T0, T0.AddHours(1));
            await RollUpAllAsync(db, T0.AddHours(1).AddMinutes(1));
            // The commit happened, but the watermark the run saw was the old one (a crash between commit and the next read).
            (await db.MetricRollupStates.SingleAsync(s => s.Id == "container/10")).RolledUpTo = T0;
            await db.SaveChangesAsync();
            await RollUpAllAsync(db, T0.AddHours(1).AddMinutes(1));
        }

        await using var check = Db();
        var ten = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 10).ToListAsync();
        Assert.Equal(6, ten.Count);
        Assert.Equal(120, ten.Sum(r => r.SampleCount));
    }

    [Fact]
    public async Task Retention_keeps_the_first_tier_while_the_second_fails_and_the_restart_rolls_it_up_completely()
    {
        var now = T0.AddDays(3).AddMinutes(1);
        var spike = T0.AddHours(5);
        await using (var db = Db()) await SeedContainerAsync(db, T0, T0.AddDays(3), t => InSpike(t, spike) ? 99 : 10);
        const int total = 3 * 2880 + 1;
        var shortRetention = MetricRetention.Default with { RawDays = 1, TenMinuteDays = 1, HourDays = 1 };
        var hourFails = new MetricRollupService { BeforeCommit = (_, tier, _) => tier == 60 ? throw new TimeoutException("disk busy") : Task.CompletedTask };

        await using (var db = Db())
        {
            // The collector retries every cycle: the 10-minute tier catches up, the hour tier fails each time.
            for (var i = 0; i < 20; i++)
                await Assert.ThrowsAsync<TimeoutException>(() => hourFails.RollUpAsync(db, now, default));
            await hourFails.PruneAsync(db, now, shortRetention, default);
        }

        await using (var db = Db())
        {
            // Raw → first tier: the raw samples older than a day are gone, but only because their 10-minute windows are
            // committed. First → second tier: the 10-minute windows are past their retention too, but the hour tier that
            // would take them over failed — they all stay.
            Assert.Equal(2879, await db.ContainerMetrics.CountAsync());   // the last day (now − 1 d = 2 d 00:01 … 3 d 00:00)
            Assert.Equal(total - 1, (await db.ContainerMetricRollups.Where(r => r.TierMinutes == 10).ToListAsync()).Sum(r => r.SampleCount));
            Assert.False(await db.ContainerMetricRollups.AnyAsync(r => r.TierMinutes == 60));
        }

        // Restart: a new service, the watermarks from disk.
        await using (var db = Db())
        {
            await RollUpAllAsync(db, now);
            await new MetricRollupService().PruneAsync(db, now, shortRetention, default);
        }

        await using var check = Db();
        var hours = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 60).ToListAsync();
        var eights = await check.ContainerMetricRollups.Where(r => r.TierMinutes == 480).ToListAsync();
        Assert.Equal(99, eights.Max(r => r.CpuMax));                                  // the spike of day 1 survived
        Assert.Equal(total - 1, eights.Sum(r => r.SampleCount)
            + (await check.ContainerMetricRollups.Where(r => r.TierMinutes == 60 && r.WindowStart >= T0.AddDays(3)).ToListAsync()).Sum(r => r.SampleCount));
        Assert.All(hours, h => Assert.True(h.WindowStart >= now.AddDays(-1).AddHours(-8), "hour windows older than their retention and covered are pruned"));
        Assert.False(await check.ContainerMetricRollups.AnyAsync(r => r.TierMinutes == 10 && r.WindowStart < now.AddDays(-1)));
    }

    [Fact]
    public async Task The_alert_history_keeps_its_own_retention()
    {
        await using var db = Db();
        db.AlertHistory.AddRange(
            new AlertHistoryEntity { AlertType = "high_cpu", Timestamp = T0.AddDays(-20) },
            new AlertHistoryEntity { AlertType = "high_cpu", Timestamp = T0.AddDays(-40) });
        await db.SaveChangesAsync();

        // Raw retention of one day does not shorten the alert history (30 days by default).
        await new MetricRollupService().PruneAsync(db, T0, MetricRetention.Default with { RawDays = 1 }, default);

        Assert.Equal([T0.AddDays(-20)], await db.AlertHistory.Select(a => a.Timestamp).ToListAsync());
    }

    [Fact]
    public async Task On_upgrade_existing_raw_data_stays_readable_and_is_rolled_up_once()
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={_path}", o => o.MigrationsAssembly("Whiskers.Migrations.Sqlite")).Options;
        await using (var db = new MetricsDbContext(options))
        {
            // The schema of the release before R1-55, with its raw samples.
            await db.GetService<IMigrator>().MigrateAsync("20261103004628_AddHistoryProcessInstance");
            Assert.DoesNotContain(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("MetricRollups"));
            await SeedContainerAsync(db, T0, T0.AddHours(3));
        }

        await using (var db = new MetricsDbContext(options))
        {
            await db.Database.MigrateAsync();
            Assert.Equal(361, await db.ContainerMetrics.CountAsync());
            await RollUpAllAsync(db, T0.AddHours(3).AddMinutes(1));
            Assert.Equal(0, (await new MetricRollupService().RollUpAsync(db, T0.AddHours(3).AddMinutes(1), default)).Windows);
        }

        await using var check = new MetricsDbContext(options);
        Assert.Equal(361, await check.ContainerMetrics.CountAsync());
        Assert.Equal(360, (await check.ContainerMetricRollups.Where(r => r.TierMinutes == 10).ToListAsync()).Sum(r => r.SampleCount));
        Assert.Equal(360, (await check.ContainerMetricRollups.Where(r => r.TierMinutes == 60).ToListAsync()).Sum(r => r.SampleCount));
    }

    // ---------------------------------------------------------------- query

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private MetricsQueryService Query(DateTime now)
    {
        var services = new ServiceCollection()
            .AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_path}"))
            .BuildServiceProvider();
        return new MetricsQueryService(services, new FixedTime(now));
    }

    /// <summary>30 days of server samples (one a minute, CPU 10 %), a 2-hour outage on day 25, a 2-minute spike of 99 %
    /// on day 20 and 77 % in the last two minutes; rolled up as the collector would and pruned with the default
    /// retention, so the raw samples of day 20 are gone.</summary>
    private async Task<(DateTime Now, List<(DateTime At, double Cpu)> Samples)> SeedThirtyDaysAsync()
    {
        var now = T0.AddDays(30).AddMinutes(3);
        var outage = T0.AddDays(25);
        var spike = T0.AddDays(20).AddHours(13).AddMinutes(37);
        var samples = new List<(DateTime, double)>();
        await using var db = Db();
        for (var t = T0; t <= now; t = t.AddMinutes(1))
        {
            if (t >= outage && t < outage.AddHours(2)) continue;
            var cpu = InSpike(t, spike) ? 99 : t > now.AddMinutes(-2) ? 77 : 10;
            samples.Add((t, cpu));
            db.ServerMetrics.Add(new ServerMetricEntity { ServerId = "s1", ServerName = "Badwolf", Timestamp = t, CpuPercent = cpu,
                MemoryUsedBytes = 40, MemoryTotalBytes = 100 });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await RollUpAllAsync(db, now);
        await new MetricRollupService().PruneAsync(db, now, MetricRetention.Default, default);
        Assert.False(await db.ServerMetrics.AnyAsync(m => m.Timestamp < now.AddDays(-8)));
        return (now, samples);
    }

    [Fact]
    public async Task A_thirty_day_chart_shows_the_true_peak_of_a_short_spike_and_the_last_minutes()
    {
        var (now, samples) = await SeedThirtyDaysAsync();

        var points = await Query(now).GetServerCpuHistoryAsync("s1", TimeSpan.FromDays(30));

        Assert.InRange(points.Count, 2, MetricsQueryService.MaxPoints);
        Assert.Equal(99, points.Max(p => p.Max));               // the 2-minute spike of day 20, from the 8-hour tier
        Assert.Equal(77, points[^1].Max);                       // the last minutes, from the raw tail
        Assert.Equal(points.Select(p => p.Timestamp).Distinct().Count(), points.Count);
        Assert.True(points.Zip(points.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
        // Whole range, no gap, no duplicate: every sample counted once; the average weighted by sample count.
        Assert.Equal(samples.Count, points.Sum(p => p.Samples));
        Assert.Equal(samples.Average(s => s.Cpu), MetricSummary.Average(points), 6);
    }

    [Fact]
    public async Task The_mcp_tool_reports_the_true_peak_over_thirty_days()
    {
        var (now, samples) = await SeedThirtyDaysAsync();
        var caller = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    Array.Empty<System.Security.Claims.Claim>(), Whiskers.Services.Auth.AuthConstants.AuthDisabledScheme))
            }
        };

        var text = await MonitoringTools.GetServerMetrics(caller, null!, Query(now), "s1", "30d");

        Assert.Contains("max 99.0%", text);
        Assert.Contains($"avg {samples.Average(s => s.Cpu).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%", text);
        Assert.Contains("Invalid period", await MonitoringTools.GetServerMetrics(caller, null!, Query(now), "s1", "90d"));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 0)]
    [InlineData(24, 10)]
    [InlineData(24 * 7, 60)]
    [InlineData(24 * 30, 60)]
    public void The_tier_is_chosen_by_range(int hours, int tier) =>
        Assert.Equal(tier, MetricsQueryService.PreferredTier(TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(24 * 7)]
    public async Task Every_range_is_covered_once_across_the_tier_boundaries(int hours)
    {
        var (now, samples) = await SeedThirtyDaysAsync();
        var since = now.AddHours(-hours);
        var segments = await Plan(now, since, MetricsQueryService.PreferredTier(TimeSpan.FromHours(hours)));

        // Adjacent, in order, the newest raw.
        Assert.True(segments.Zip(segments.Skip(1)).All(s => s.First.To == s.Second.From), string.Join(" | ", segments));
        Assert.Equal(0, segments[^1].Tier);

        var points = await Query(now).GetServerCpuHistoryAsync("s1", TimeSpan.FromHours(hours));
        // A window overlapping the range's start is included whole: count from its start.
        var from = segments[0].Tier == 0 ? since : segments[0].From;
        Assert.Equal(samples.Count(s => s.At >= from), points.Sum(p => p.Samples));
        Assert.Equal(samples.Where(s => s.At >= since).Max(s => s.Cpu), points.Max(p => p.Max));
    }

    private async Task<List<MetricsQueryService.Segment>> Plan(DateTime now, DateTime since, int preferred)
    {
        await using var db = Db();
        return await MetricsQueryService.PlanAsync(db, new ServerSeries(), new SeriesKey("s1", ""), since, now, preferred, default);
    }

    [Fact]
    public async Task A_series_newer_than_the_coarse_watermark_is_read_from_the_finer_tier_without_a_gap()
    {
        // A container that started after the last hour window closed: it has 10-minute windows and raw samples only.
        var start = T0.AddMinutes(35);
        await using (var db = Db())
        {
            await SeedContainerAsync(db, start, T0.AddMinutes(58));
            await RollUpAllAsync(db, T0.AddMinutes(59));
        }

        var points = await Query(T0.AddMinutes(59)).GetContainerCpuHistoryAsync("c1", "s1", TimeSpan.FromDays(7));
        Assert.Equal(47, points.Sum(p => p.Samples));
    }

    [Fact]
    public void Thinning_keeps_each_points_maximum_and_weights_the_average()
    {
        var points = Enumerable.Range(0, 1000).Select(i => new MetricPoint
        {
            Timestamp = T0.AddMinutes(i), Value = 10, Max = i == 501 ? 100 : 10, Samples = i % 2 == 0 ? 1 : 3,
        }).ToList();
        points[500].Value = 50;   // one sample of 50 next to a window of three averaging 10

        var thin = MetricsQueryService.Thin(points, 100);

        Assert.Equal(100, thin.Count);
        Assert.Equal(100, thin.Max(p => p.Max));
        Assert.Equal(2000, thin.Sum(p => p.Samples));
        var merged = thin.Single(p => p.Max == 100);
        Assert.Equal(20, merged.Samples);                      // 5 points of weight 1 and 5 of weight 3 …
        Assert.Equal((50 + 10.0 * 19) / 20, merged.Value, 9);   // … one of them 50: weighted, not the mean of the 10 points
    }

    // ---------------------------------------------------------------- review round 1

    /// <summary>c1: 3 days and 30 minutes, a 2-minute spike of 99 % at day 2 00:10; c2: only since 3 d 00:12, after the
    /// last closed hour window. Rolled up at <paramref name="now"/>, then pruned with raw and 10-minute retention of one
    /// day — so the 10-minute windows of 2 d 00:00–00:30 are gone and the hour window 2 d 00:00 is the only one holding
    /// the spike.</summary>
    private async Task SeedPrunedPartialWindowAsync(DateTime now)
    {
        var spike = T0.AddDays(2).AddMinutes(10);
        await using var db = Db();
        await SeedContainerAsync(db, T0, now, t => InSpike(t, spike) ? 99 : 10);
        await SeedContainerAsync(db, T0.AddDays(3).AddMinutes(12), now, container: "c2");
        await RollUpAllAsync(db, now);
        await new MetricRollupService().PruneAsync(db, now,
            MetricRetention.Default with { RawDays = 1, TenMinuteDays = 1 }, default);
        Assert.False(await db.ContainerMetricRollups.AnyAsync(r => r.TierMinutes == 10 && r.ContainerId == "c1" && r.WindowStart < T0.AddDays(2).AddMinutes(30)));
    }

    [Fact]
    public async Task A_range_starting_inside_a_pruned_partial_window_comes_out_whole_from_the_coarser_tier()
    {
        var now = T0.AddDays(3).AddMinutes(30);
        await SeedPrunedPartialWindowAsync(now);

        // 24 h from 2 d 00:30: the 10-minute tier begins exactly there, mid-hour. The hour window 2 d 00:00 serves the
        // part before the first whole hour of the 10-minute tier — whole, so with its spike and all its samples.
        var points = await Query(now).GetContainerCpuHistoryAsync("c1", "s1", TimeSpan.FromHours(24));

        var expected = (int)((now - T0.AddDays(2)).TotalSeconds / 30) + 1;   // every c1 sample from 2 d 00:00 on
        Assert.Equal(expected, points.Sum(p => p.Samples));
        Assert.Equal(99, points.Max(p => p.Max));

        await using var db = Db();
        var segments = await MetricsQueryService.PlanAsync(db, new ContainerSeries(), new SeriesKey("s1", "c1"),
            now.AddHours(-24), now, 10, default);
        Assert.Equal([(60, T0.AddDays(2), T0.AddDays(2).AddHours(1)), (10, T0.AddDays(2).AddHours(1), T0.AddDays(3).AddMinutes(30))],
            segments.Where(s => s.Tier != 0).Select(s => (s.Tier, s.From, s.To)));
    }

    [Fact]
    public async Task A_container_newer_than_the_coarser_watermark_is_served_by_the_finer_tier_from_that_watermark()
    {
        var now = T0.AddDays(3).AddMinutes(30);
        await SeedPrunedPartialWindowAsync(now);

        // c2's first 10-minute window (3 d 00:10) rounds up to 3 d 01:00, past the hour tier's watermark (3 d 00:00):
        // the 10-minute tier serves from the watermark, or c2's first 20 minutes would fall between the tiers.
        await using (var db = Db())
        {
            var segments = await MetricsQueryService.PlanAsync(db, new ContainerSeries(), new SeriesKey("s1", "c2"),
                now.AddHours(-24), now, 10, default);
            Assert.Contains(segments, s => s.Tier == 10 && s.From == T0.AddDays(3));
        }

        var points = await Query(now).GetContainerCpuHistoryAsync("c2", "s1", TimeSpan.FromHours(24));
        Assert.Equal(37, points.Sum(p => p.Samples));   // 3 d 00:12 … 00:30 every 30 s
    }

    [Fact]
    public async Task A_clock_set_back_pauses_the_roll_up_with_one_warning_and_the_chart_keeps_the_data_since()
    {
        await using (var db = Db())
        {
            await SeedContainerAsync(db, T0, T0.AddHours(2));
            await RollUpAllAsync(db, T0.AddHours(2).AddMinutes(1));
            // Rolled up under a clock five hours ahead: its window and watermark lie in the future now.
            await SeedContainerAsync(db, T0.AddHours(4), T0.AddHours(4).AddMinutes(9.5), _ => 55);
            db.ContainerMetricRollups.Add(new ContainerMetricRollupEntity { TierMinutes = 10, ServerId = "s1", ContainerId = "c1",
                ContainerName = "web", WindowStart = T0.AddHours(4), SampleCount = 20, CpuAvg = 55, CpuMax = 55 });
            (await db.MetricRollupStates.SingleAsync(s => s.Id == "container/10")).RolledUpTo = T0.AddHours(5);
            await db.SaveChangesAsync();
            // The clock is corrected: samples since then lie behind that watermark.
            await SeedContainerAsync(db, T0.AddHours(2).AddSeconds(30), T0.AddHours(2).AddMinutes(30), _ => 33);
            // A container first seen after the correction, mid-window: no 10-minute window of its own exists.
            await SeedContainerAsync(db, T0.AddHours(2).AddMinutes(3), T0.AddHours(2).AddMinutes(30), _ => 21, container: "c3");
        }

        var now = T0.AddHours(2).AddMinutes(31);
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        using (var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logs)))
        {
            var service = new MetricRollupService(factory.CreateLogger<MetricRollupService>());
            await using var db = Db();
            var before = await db.ContainerMetricRollups.CountAsync();
            await service.RollUpAsync(db, now, default);
            await service.RollUpAsync(db, now.AddMinutes(10), default);
            Assert.Single(logs.Lines, l => l.Contains("ahead of the clock"));
            Assert.True(service.ClockBehind("container"));
            Assert.Equal(before, await db.ContainerMetricRollups.CountAsync());   // nothing rewritten, nothing rewound
        }

        // The 24-hour chart shows the samples since the correction — every raw sample up to now, once.
        var points = await Query(now).GetContainerCpuHistoryAsync("c1", "s1", TimeSpan.FromHours(24));
        Assert.Equal(241 + 60, points.Sum(p => p.Samples));
        Assert.Equal(33, points.Max(p => p.Max));
        Assert.Equal(33, points[^1].Max);

        // … also for a series first seen during the episode, from its very first sample (2 h 03 … 2 h 30).
        var c3 = await Query(now).GetContainerCpuHistoryAsync("c3", "s1", TimeSpan.FromHours(24));
        Assert.Equal(55, c3.Sum(p => p.Samples));
    }

    [Fact]
    public async Task Nothing_below_a_watermark_ahead_of_the_clock_is_pruned()
    {
        await using var db = Db();
        await SeedContainerAsync(db, T0, T0.AddHours(1));
        await RollUpAllAsync(db, T0.AddHours(1).AddMinutes(1));
        (await db.MetricRollupStates.SingleAsync(s => s.Id == "container/10")).RolledUpTo = T0.AddDays(10);
        await db.SaveChangesAsync();

        await new MetricRollupService().PruneAsync(db, T0.AddDays(3), MetricRetention.Default with { RawDays = 1 }, default);

        Assert.Equal(121, await db.ContainerMetrics.CountAsync());
    }
}
