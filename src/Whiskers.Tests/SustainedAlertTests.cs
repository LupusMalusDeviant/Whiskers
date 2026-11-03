using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Docker;
using Whiskers.Services.Metrics;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-56: one documented semantics for every threshold alert — the container and disk alerts migrated onto the host rules'
/// <see cref="BreachTracker"/>. Duration is elapsed time, not a sample count: a reading at or above the threshold starts or
/// continues a breach, one inside the hysteresis band neither resets nor clears it, and the breach ends only after readings
/// stayed below the threshold minus <see cref="BreachRules.PercentClearMargin"/> for <see cref="BreachRules.DefaultClearedFor"/>,
/// with one Cleared finding if it was reported. A gap longer than the tolerance resets the breach and is reported as a
/// <c>metrics_gap</c>. <c>CooldownMinutes</c> is the least gap between two breaches; a lasting breach is raised once.
/// Every test runs on a fake clock.
/// </summary>
public sealed class SustainedAlertTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BreachRules Rules = BreachRules.Threshold(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15),
        BreachRules.MinimumGapTolerance);

    // --- the duration model, on the tracker ------------------------------------------------------------------------

    private static List<HostLoadFinding> Drive(BreachTracker tracker, IEnumerable<(TimeSpan At, double Value)> readings,
        BreachRules? rules = null) =>
        readings.Select(r => tracker.Consider(T0 + r.At, "c1", "web", NotificationEventTypes.HighCpu, r.Value, 90,
                (v, open) => $"CPU {v:F0}% for {open.TotalMinutes:F0} min", v => $"CPU back to {v:F0}%", rules ?? Rules))
            .OfType<HostLoadFinding>().ToList();

    private static IEnumerable<(TimeSpan, double)> Every(TimeSpan step, TimeSpan from, TimeSpan to, double value)
    {
        for (var at = from; at < to; at += step) yield return (at, value);
    }

    private static BreachTracker Tracker() => new(new HostLoadThresholds());

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(90)]
    public void A_breach_is_raised_after_elapsed_time_whatever_the_sampling_interval(int seconds)
    {
        var findings = Drive(Tracker(), Every(TimeSpan.FromSeconds(seconds), TimeSpan.Zero, TimeSpan.FromMinutes(10), 95));

        var raised = Assert.Single(findings);
        Assert.Equal(FindingKind.Raised, raised.What);
        // The first reading at or past three minutes of elapsed time — never "six samples".
        var expected = TimeSpan.FromSeconds(Math.Ceiling(180.0 / seconds) * seconds);
        Assert.Equal(T0 + expected, raised.AtUtc);
        Assert.Equal(expected, raised.OpenFor);
    }

    [Fact]
    public void A_reading_in_the_hysteresis_band_neither_resets_nor_clears_the_breach()
    {
        var tracker = Tracker();
        // 2 minutes over, one reading inside the band (88 is below 90 but above the clear bound 85), over again.
        var findings = Drive(tracker, [
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(2), 95),
            (TimeSpan.FromMinutes(2), 88),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2.5), TimeSpan.FromMinutes(4), 95)]);
        var raised = Assert.Single(findings);
        Assert.Equal(T0.AddMinutes(3), raised.AtUtc);              // not reset by the band reading

        // Twenty minutes inside the band after the raise: no all-clear.
        Assert.Empty(Drive(tracker, Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(24), 87)));
    }

    [Fact]
    public void A_breach_ends_only_after_the_clear_duration_below_the_clear_bound_with_one_Cleared()
    {
        var tracker = Tracker();
        Drive(tracker, Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(5), 95));

        // Four minutes below the clear bound, then over again: the recovery is cancelled.
        Assert.Empty(Drive(tracker, [.. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(9), 50),
            (TimeSpan.FromMinutes(9), 95)]));
        // Five minutes below it: one Cleared, dated to when the value dropped.
        var findings = Drive(tracker, Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(20), 50));

        var cleared = Assert.Single(findings);
        Assert.Equal(FindingKind.Cleared, cleared.What);
        Assert.Equal("high_cpu_recovered", cleared.EventType);
        Assert.Equal(T0.AddMinutes(14.5), cleared.AtUtc);
        Assert.Equal(TimeSpan.FromMinutes(9.5), cleared.OpenFor);
    }

    [Fact]
    public void A_value_flapping_at_the_threshold_raises_once_and_never_clears()
    {
        // The flapping BreachTracker's class comment describes: 91, 89, 91, 89 … for an hour.
        var readings = Enumerable.Range(0, 120).Select(i => (TimeSpan.FromSeconds(30 * i), i % 2 == 0 ? 91.0 : 89.0));

        var findings = Drive(Tracker(), readings);

        Assert.Equal(FindingKind.Raised, Assert.Single(findings).What);
    }

    [Fact]
    public void A_lasting_breach_is_raised_once_and_not_again_after_the_cooldown()
    {
        // Before R1-56 high_cpu re-fired every CooldownMinutes while the breach lasted.
        var findings = Drive(Tracker(), Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromHours(2), 99));

        Assert.Equal(FindingKind.Raised, Assert.Single(findings).What);
    }

    [Fact]
    public void A_breach_that_ends_before_it_was_reported_ends_without_a_Cleared()
    {
        var findings = Drive(Tracker(), [.. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(2), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20), 40)]);

        Assert.Empty(findings);
    }

    [Fact]
    public void The_cooldown_is_the_minimum_gap_between_two_breaches_of_one_series()
    {
        var tracker = Tracker();
        // Breach 1: raised at 3 min, value drops at 5 min, cleared at 10 min (ended at 5 min).
        var first = Drive(tracker, [.. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(5), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(11), 40)]);
        Assert.Equal([FindingKind.Raised, FindingKind.Cleared], first.Select(f => f.What));

        // Breach 2 starts at 11 min and is reportable at 14 min — held until 15 minutes after breach 1 ended (20 min).
        var second = Drive(tracker, Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(40), 95));

        var raised = Assert.Single(second);
        Assert.Equal(T0.AddMinutes(20), raised.AtUtc);
        Assert.Equal(TimeSpan.FromMinutes(9), raised.OpenFor);      // the duration still counts from its own start
    }

    [Fact]
    public void A_gap_longer_than_the_tolerance_resets_the_breach_instead_of_bridging_it()
    {
        // Two minutes over, a five-minute gap (tolerance three), two minutes over: bridged it would be raised.
        var findings = Drive(Tracker(), [.. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(2), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(9.5), 95)]);
        Assert.Empty(findings);

        // A gap within the tolerance is bridged.
        var bridged = Drive(Tracker(), [.. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(2), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5), 95)]);
        Assert.Equal(T0.AddMinutes(4), Assert.Single(bridged).AtUtc);
    }

    [Fact]
    public void A_gap_tolerance_follows_the_interval_but_never_drops_below_three_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), BreachRules.GapToleranceFor(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMinutes(3), BreachRules.GapToleranceFor(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMinutes(15), BreachRules.GapToleranceFor(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_missing_series_is_one_metrics_gap_after_the_tolerance_and_one_recovery()
    {
        var gaps = new SeriesGapTracker();
        var tolerance = TimeSpan.FromMinutes(3);
        Assert.Null(gaps.Seen(T0, "c1", "web", "container web", tolerance));

        var findings = Enumerable.Range(1, 12)
            .Select(i => gaps.Missing(T0.AddSeconds(30 * i), "c1", "web", "container web", tolerance, "the stats call failed", ["high_cpu"]))
            .OfType<HostLoadFinding>().ToList();
        var gap = Assert.Single(findings);
        Assert.Equal((NotificationEventTypes.MetricsGap, T0.AddMinutes(3.5)), (gap.EventType, gap.AtUtc));
        Assert.Contains("the stats call failed", gap.Summary);
        Assert.Contains("high_cpu", gap.Summary);

        var back = gaps.Seen(T0.AddMinutes(7), "c1", "web", "container web", tolerance);
        Assert.Equal("metrics_gap_recovered", back!.EventType);
        Assert.Null(gaps.Seen(T0.AddMinutes(7.5), "c1", "web", "container web", tolerance));
    }

    // --- the collector, on a fake clock ----------------------------------------------------------------------------

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sw-r156-col-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestSqlite.ClearPool(_db);
        try { File.Delete(_db); } catch { /* best effort */ }
    }

    /// <summary>A collector over one Docker server with one container whose readings the test sets each cycle.</summary>
    private sealed class Rig
    {
        public required MetricsCollectorService Collector { get; init; }
        public required ManualTime Clock { get; init; }
        public required FakeNotifications Notifications { get; init; }
        public double Cpu { get; set; } = 10;
        public bool StatsFail { get; set; }
        public bool HostMissing { get; set; }
        public double DiskPercent { get; set; } = 40;
        public bool DfFails { get; set; }
        public bool ListingFails { get; set; }
        public bool Paused { get; set; }
        public TimeSpan Interval { get; init; }

        public List<NotificationEvent> Events
        {
            get { lock (Notifications.Events) return Notifications.Events.ToList(); }
        }

        /// <summary>Runs cycles until <paramref name="until"/> on the fake clock, one per interval.</summary>
        public async Task RunUntilAsync(TimeSpan until)
        {
            while (Clock.Now.UtcDateTime - T0 < until)
            {
                await Collector.CollectMetricsAsync(CancellationToken.None);
                Clock.Now += Interval;
            }
        }
    }

    private Rig NewRig(int intervalSeconds = 30, int sustainedMinutes = 3, int cooldownMinutes = 15,
        Action<MetricAlertSettings>? configure = null, string? breachFile = null, DateTime? start = null)
    {
        Rig? rig = null;
        var container = new ContainerInfo { Id = "c1", Name = "web", Image = "nginx", ServerId = "local", ServerName = "Badwolf" };
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_db}"));
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
            new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }));
        services.AddSingleton(Proxy<IDockerService>.Create((m, _) => m.Name switch
        {
            nameof(IDockerService.ListAllContainersAsync) => Task.FromResult<IList<ContainerInfo>>([container]),
            // The listing as the collector reads it: a failed server answers with no containers and is named as failed.
            nameof(IDockerService.ListAllContainersDetailedAsync) => Task.FromResult(rig!.ListingFails || rig.Paused
                ? new FleetContainerListing
                {
                    FailedServers = [new FleetServerFailure("local", "Badwolf", rig.Paused ? "paused" : "timed out", NotAsked: rig.Paused)],
                }
                : new FleetContainerListing { Containers = [container], RespondedServerIds = new HashSet<string> { "local" } }),
            _ => null,
        }));
        services.AddSingleton(Proxy<Whiskers.Services.Observability.ILoopSuspensionService>.Create((m, _) => m.Name switch
        {
            nameof(Whiskers.Services.Observability.ILoopSuspensionService.IsSuspended) => rig!.Paused,
            _ => null,
        }));
        if (breachFile is not null) services.AddSingleton<IAlertBreachStore>(new AlertBreachStore(breachFile));
        services.AddSingleton(Proxy<IMetricsSource>.Create((m, _) => m.Name switch
        {
            nameof(IMetricsSource.GetContainerStatsAsync) => rig!.StatsFail
                ? Task.FromException<ContainerStats?>(new HttpRequestException("stats failed"))
                : Task.FromResult<ContainerStats?>(new ContainerStats { ContainerId = "c1", CpuPercent = rig.Cpu }),
            nameof(IMetricsSource.GetAllServerSystemInfoAsync) => Task.FromResult(rig!.HostMissing
                ? new Dictionary<string, ServerSystemInfo>()
                : new Dictionary<string, ServerSystemInfo>
                {
                    ["local"] = new() { ServerId = "local", ServerName = "Badwolf", IsReachable = true, CpuCount = 2,
                        CpuUsagePercent = 20, MemoryTotalBytes = 1000, MemoryUsedBytes = 300 },
                }),
            _ => null,
        }));
        services.AddSingleton(Proxy<Whiskers.Services.Server.IHostCommandExecutor>.Create((_, _) =>
            Task.FromResult(rig!.DfFails ? new Whiskers.Services.Server.CommandResult { ExitCode = 1 } : new Whiskers.Services.Server.CommandResult
            {
                Output = $"/dev/sda1 1000 {(long)(rig!.DiskPercent * 10)} 0 0% /",
            })));
        var notifications = new FakeNotifications();
        services.AddSingleton<INotificationService>(notifications);
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();

        var alert = new MetricAlertSettings
        {
            CpuPercent = 90, MemoryPercent = 90, DiskPercent = 90,
            SustainedMinutes = sustainedMinutes, CooldownMinutes = cooldownMinutes,
        };
        configure?.Invoke(alert);
        var clock = new ManualTime(new DateTimeOffset(start ?? T0));
        var interval = new EffectiveInterval<MetricsSettings>(() => intervalSeconds, "Metrics:CollectionIntervalSeconds", 5,
            86_400, TimeSpan.FromSeconds(1), NullLogger.Instance);
        var collector = new MetricsCollectorService(sp,
            new StaticOptionsMonitor<MetricAlertSettings>(alert),
            new HostLoadEvaluator(), new ApiLatencyEvaluator(), new RollingBaseline(),
            new StaticOptionsMonitor<MetricsSettings>(new MetricsSettings()),
            NullLogger<MetricsCollectorService>.Instance,
            new Whiskers.Services.Observability.SelfMetrics.SelfMetrics(), interval, time: clock);
        rig = new Rig { Collector = collector, Clock = clock, Notifications = notifications, Interval = TimeSpan.FromSeconds(intervalSeconds) };
        return rig;
    }

    private static List<NotificationEvent> OfType(IEnumerable<NotificationEvent> events, string type) =>
        events.Where(e => e.EventType == type).ToList();

    [Fact]
    public async Task The_collector_raises_high_cpu_once_per_breach_and_closes_it_with_a_recovery()
    {
        var rig = NewRig();
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(60));            // an hour over: one raise, not one every cooldown
        rig.Cpu = 20;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(70));

        var raised = Assert.Single(OfType(rig.Events, NotificationEventTypes.HighCpu));
        Assert.Equal(T0.AddMinutes(3), raised.Timestamp);
        Assert.Equal(("c1", "web", "local"), (raised.ContainerId, raised.ContainerName, raised.ServerId));
        Assert.Equal("web: CPU at 97% for 3 minutes (threshold 90%).", raised.ImageInfo);
        var cleared = Assert.Single(OfType(rig.Events, "high_cpu_recovered"));
        Assert.Equal(T0.AddMinutes(65), cleared.Timestamp);           // five minutes after the value dropped at 60
        Assert.StartsWith("web: CPU back to 20%. It had been over the threshold for 60 minutes.", cleared.ImageInfo);
        Assert.Equal("CPU load back to normal", NotificationFormatter.Describe(cleared).Title);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task A_changed_collection_interval_does_not_change_the_effective_duration(int seconds)
    {
        var rig = NewRig(intervalSeconds: seconds);
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(10));

        // Raised after three minutes of elapsed time — the old sample count (SustainedMinutes × 2) would have taken one
        // minute at 10 s and six minutes at 60 s.
        Assert.Equal(T0.AddMinutes(3), Assert.Single(OfType(rig.Events, NotificationEventTypes.HighCpu)).Timestamp);
    }

    [Fact]
    public async Task A_container_whose_stats_call_fails_is_reported_as_a_gap_and_its_breach_starts_again()
    {
        var rig = NewRig(cooldownMinutes: 1);
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(5));                 // high_cpu at 3 min
        rig.StatsFail = true;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(12));                // only a log warning before R1-56
        rig.StatsFail = false;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(20));

        // What the operator sees: the gap, once, naming the alert it leaves unconfirmed; its end; and a new raise only
        // after the breach was confirmed again by three minutes of new readings — the gap was not bridged.
        var gap = Assert.Single(OfType(rig.Events, NotificationEventTypes.MetricsGap));
        Assert.Equal(T0.AddMinutes(8), gap.Timestamp);                    // last reading 4:30, more than 3 min without
        Assert.Equal(("c1", "web"), (gap.ContainerId, gap.ContainerName));
        Assert.Equal("No readings: alerts cannot fire for this", NotificationFormatter.Describe(gap).Title);
        Assert.Contains("No readings of container web", gap.ImageInfo);
        Assert.Contains("the stats call failed", gap.ImageInfo);
        Assert.Contains("high_cpu", gap.ImageInfo);
        Assert.Equal(T0.AddMinutes(12), Assert.Single(OfType(rig.Events, "metrics_gap_recovered")).Timestamp);
        var raises = OfType(rig.Events, NotificationEventTypes.HighCpu);
        Assert.Equal([T0.AddMinutes(3), T0.AddMinutes(15)], raises.Select(e => e.Timestamp));
        Assert.Empty(OfType(rig.Events, "high_cpu_recovered"));          // a gap is no recovery
    }

    [Fact]
    public async Task A_host_without_a_reading_is_one_gap_for_the_host_not_one_per_series()
    {
        var rig = NewRig();
        await rig.RunUntilAsync(TimeSpan.FromMinutes(1));
        rig.HostMissing = true;                                            // node_exporter down
        await rig.RunUntilAsync(TimeSpan.FromMinutes(10));
        rig.HostMissing = false;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(12));

        var gap = Assert.Single(OfType(rig.Events, NotificationEventTypes.MetricsGap));
        Assert.Equal(("local", "Badwolf"), (gap.ServerId, gap.ServerName));
        Assert.Contains("Badwolf's CPU, memory and disk", gap.ImageInfo);
        Assert.Equal("servers", NotificationFormatter.LinkFor(gap));
        Assert.Single(OfType(rig.Events, "metrics_gap_recovered"));
    }

    [Fact]
    public async Task High_disk_is_raised_once_per_breach_and_cleared()
    {
        var rig = NewRig();
        rig.DiskPercent = 95;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(40));
        rig.DiskPercent = 50;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(50));

        var raised = Assert.Single(OfType(rig.Events, NotificationEventTypes.HighDisk));
        Assert.Equal(("local", T0.AddMinutes(3)), (raised.ServerId, raised.Timestamp));
        Assert.Single(OfType(rig.Events, "high_disk_recovered"));
    }

    [Fact]
    public async Task Metric_anomaly_is_a_point_in_time_outlier_that_keeps_re_firing_after_the_cooldown()
    {
        var rig = NewRig(cooldownMinutes: 5, configure: a =>
        {
            a.AnomalyEnabled = true;
            a.AnomalyWindow = 5;
            a.AnomalyFloorPercent = 40;
            a.CpuPercent = 99.5;                                           // keep the threshold rule out of it
        });

        // Five quiet readings and a spike, over and over: one outlier every three minutes, the cooldown five minutes.
        for (var round = 0; round < 6; round++)
        {
            foreach (var cpu in new[] { 10.0, 12, 10, 12, 10 })
            {
                rig.Cpu = cpu;
                await rig.RunUntilAsync(rig.Clock.Now.UtcDateTime - T0 + rig.Interval);
            }
            rig.Cpu = 95;
            await rig.RunUntilAsync(rig.Clock.Now.UtcDateTime - T0 + rig.Interval);
        }

        var anomalies = OfType(rig.Events, NotificationEventTypes.MetricAnomaly);
        Assert.True(anomalies.Count >= 2, $"expected repeated anomalies, got {anomalies.Count}");
        Assert.All(anomalies.Zip(anomalies.Skip(1)), p => Assert.True(p.Second.Timestamp - p.First.Timestamp >= TimeSpan.FromMinutes(5)));
        Assert.DoesNotContain(rig.Events, e => e.EventType.StartsWith("metric_anomaly_", StringComparison.Ordinal));
    }

    // --- review round 1 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_listing_that_fails_for_one_cycle_neither_forgets_nor_raises_the_breach_again()
    {
        // MAJOR-2: one 8-second listing timeout during an open breach dropped its state — no recovery ever, and a second
        // raise of the same breach once it was confirmed again.
        var rig = NewRig();
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(5));                 // high_cpu at 3 min
        rig.ListingFails = true;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(5.5));               // one cycle
        rig.ListingFails = false;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(20));
        rig.Cpu = 20;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(30));

        Assert.Equal(T0.AddMinutes(3), Assert.Single(OfType(rig.Events, NotificationEventTypes.HighCpu)).Timestamp);
        Assert.Single(OfType(rig.Events, "high_cpu_recovered"));
        Assert.Empty(OfType(rig.Events, NotificationEventTypes.MetricsGap));
    }

    [Fact]
    public async Task A_listing_that_keeps_failing_is_one_gap_for_the_servers_containers()
    {
        var rig = NewRig();
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(5));
        rig.ListingFails = true;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(12));
        rig.ListingFails = false;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(14));

        var gap = Assert.Single(OfType(rig.Events, NotificationEventTypes.MetricsGap));
        Assert.Equal(("local", T0.AddMinutes(8.5)), (gap.ServerId, gap.Timestamp));   // the last listing at 4:30 + more than 3 min
        Assert.Contains("the containers of Badwolf", gap.ImageInfo);
        Assert.Contains("listing its containers failed", gap.ImageInfo);
        Assert.Contains("high_cpu (web)", gap.ImageInfo);
        Assert.Equal(T0.AddMinutes(12), Assert.Single(OfType(rig.Events, "metrics_gap_recovered")).Timestamp);
    }

    [Fact]
    public async Task A_held_up_cycle_is_one_message_per_server_with_an_open_alert_not_one_per_series()
    {
        // MAJOR-3: one cycle ten minutes late used to raise a metrics_gap for the container, the host and its disk at once
        // — per series, past every throttle.
        var rig = NewRig();
        await rig.RunUntilAsync(TimeSpan.FromMinutes(2));
        rig.Clock.Now += TimeSpan.FromMinutes(10);                         // nothing open: a log line only
        await rig.RunUntilAsync(TimeSpan.FromMinutes(14));
        Assert.Empty(rig.Events);

        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(20));                 // high_cpu at 17:30
        rig.Clock.Now += TimeSpan.FromMinutes(10);
        await rig.RunUntilAsync(TimeSpan.FromMinutes(31));

        Assert.Empty(OfType(rig.Events, NotificationEventTypes.MetricsGap));
        var held = Assert.Single(OfType(rig.Events, "metrics_gap_recovered"));
        Assert.Equal(("local", "Badwolf"), (held.ServerId, held.ServerName));
        Assert.Contains("Metrics collection was held up", held.ImageInfo);
        Assert.Contains("high_cpu (web)", held.ImageInfo);
    }

    [Fact]
    public async Task A_restart_during_a_breach_resumes_it_closes_it_once_and_never_raises_it_twice()
    {
        // MAJOR-4: the breach state lived only in memory — after a restart the raise was never closed, and a lasting
        // breach was raised a second time as if it were new.
        var file = Path.Combine(Path.GetTempPath(), $"sw-r156-breaches-{Guid.NewGuid():N}.json");
        try
        {
            var before = NewRig(breachFile: file);
            before.Cpu = 97;
            await before.RunUntilAsync(TimeSpan.FromMinutes(5));
            Assert.Single(OfType(before.Events, NotificationEventTypes.HighCpu));

            // Whiskers is back a minute later: a new collector over the same file.
            var after = NewRig(breachFile: file, start: T0.AddMinutes(6));
            after.Cpu = 97;
            await after.RunUntilAsync(TimeSpan.FromMinutes(30));
            after.Cpu = 20;
            await after.RunUntilAsync(TimeSpan.FromMinutes(40));

            Assert.Empty(OfType(after.Events, NotificationEventTypes.HighCpu));
            var cleared = Assert.Single(OfType(after.Events, "high_cpu_recovered"));
            Assert.Equal(T0.AddMinutes(35), cleared.Timestamp);
            Assert.Empty(OfType(after.Events, NotificationEventTypes.MetricsGap));
        }
        finally
        {
            try { File.Delete(file); File.Delete(file + ".bak"); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_restored_breach_is_closed_by_readings_and_a_gap_after_the_restart_still_resets_it()
    {
        var tracker = Tracker();
        var raised = Drive(tracker, Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(5), 95));
        Assert.Single(raised);
        var saved = tracker.Snapshot();
        Assert.Equal(("c1", NotificationEventTypes.HighCpu), (Assert.Single(saved).Series, saved[0].Kind));

        var restored = Tracker();
        restored.Restore(saved, T0.AddMinutes(20));                        // a 15-minute downtime is not a gap of the series
        var after = Drive(restored, [.. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(40), 40)]);
        Assert.Equal(FindingKind.Cleared, Assert.Single(after).What);

        // A kind that is no breach kind, or a malformed series, is not taken over.
        var strict = Tracker();
        strict.Restore([saved[0] with { Kind = "server_unreachable" }, saved[0] with { Series = "a|b" }], T0);
        Assert.Empty(strict.Snapshot());
    }

    [Fact]
    public async Task A_df_that_never_answered_is_no_gap_but_one_that_stopped_answering_is()
    {
        // MINOR-5: a host without a command channel got a permanent metrics_gap after every start.
        var never = NewRig();
        never.DfFails = true;
        await never.RunUntilAsync(TimeSpan.FromMinutes(15));
        Assert.Empty(never.Events);

        var stopped = NewRig();
        await stopped.RunUntilAsync(TimeSpan.FromMinutes(2));
        stopped.DfFails = true;
        await stopped.RunUntilAsync(TimeSpan.FromMinutes(10));
        var gap = Assert.Single(OfType(stopped.Events, NotificationEventTypes.MetricsGap));
        Assert.Contains("df returned no reading", gap.ImageInfo);
    }

    [Fact]
    public async Task A_paused_server_is_no_metrics_gap_while_paused_or_on_resume()
    {
        // MINOR-6: loops_paused already says nothing is checked there; a metrics_gap on top of it was noise.
        var rig = NewRig();
        rig.Cpu = 97;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(5));
        rig.Paused = true;
        rig.HostMissing = true;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(30));
        Assert.Empty(OfType(rig.Events, NotificationEventTypes.MetricsGap));

        rig.Paused = false;
        rig.HostMissing = false;
        await rig.RunUntilAsync(TimeSpan.FromMinutes(31));
        Assert.Empty(OfType(rig.Events, "metrics_gap_recovered"));
        Assert.Single(OfType(rig.Events, NotificationEventTypes.HighCpu));
    }

    [Fact]
    public void A_breach_held_by_the_minimum_gap_that_ends_before_the_gap_passes_is_never_raised()
    {
        // MINOR-9: "raised once that gap has passed, if it is still going on" — and not at all when it is not.
        var tracker = Tracker();
        var first = Drive(tracker, [.. Every(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(5), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(11), 40)]);
        Assert.Equal([FindingKind.Raised, FindingKind.Cleared], first.Select(f => f.What));

        // Breach 2 runs 11–17 min (reportable at 14, held until 20) and is over before then.
        var second = Drive(tracker, [.. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(17), 95),
            .. Every(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(17), TimeSpan.FromMinutes(40), 40)]);
        Assert.Empty(second);
    }

    // --- transitions bypass the per-channel throttle ----------------------------------------------------------------

    [Fact]
    public async Task Two_breaches_and_their_recoveries_inside_one_throttle_window_all_reach_the_channel()
    {
        var handler = new NotificationDeliveryTests.ScriptedHandler();
        var slack = new SlackNotificationService(new HttpClient(handler), new StaticOptionsMonitor<SlackSettings>(new SlackSettings
        {
            Enabled = true, WebhookUrl = "https://hooks.slack.com/services/T0/B0/x", ThrottleMinutes = 60,
        }), NullLogger<SlackNotificationService>.Instance);
        var composite = NotificationDeliveryTests.Composite([slack], outbox: null);

        foreach (var type in new[] { "high_cpu", "high_cpu_recovered", "high_cpu", "high_cpu_recovered" })
            await composite.SendAsync(new NotificationEvent { EventType = type, ContainerId = "c1", ContainerName = "web", ServerId = "local" });
        Assert.Equal(4, handler.Requests);

        // A non-transition is still throttled: the throttle itself is unchanged.
        await composite.SendAsync(new NotificationEvent { EventType = "log_rotation_missing", ContainerId = "c1", ServerId = "local" });
        await composite.SendAsync(new NotificationEvent { EventType = "log_rotation_missing", ContainerId = "c1", ServerId = "local" });
        Assert.Equal(5, handler.Requests);
    }

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A fake of any interface: each call goes to <paramref name="handler"/>; a Task-returning call it does not
    /// answer completes with the default.</summary>
    internal class Proxy<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _handler = (_, _) => null;

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Proxy<T>>();
            ((Proxy<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = _handler(method!, args);
            if (result is not null) return result;
            var type = method!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
