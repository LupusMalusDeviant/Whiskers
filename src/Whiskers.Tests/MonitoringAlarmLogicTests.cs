using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Cve;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;
using Whiskers.Services.Observability;
using Whiskers.Services.Observability.Outcomes;
using Whiskers.Services.Persistence;
using SelfMetricsImpl = Whiskers.Services.Observability.SelfMetrics.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// Work package T1 — the alarm logic of monitoring (review 1.0.0, area Monitoring). Each test names the defect it pins;
/// each failed on main 6892b008 before its fix. The rule behind all of them: an alarm that cannot be judged stays
/// visible rather than silently closed, and "unknown" is never "clean".
/// </summary>
public sealed class MonitoringAlarmLogicTests
{
    private static readonly DateTime Noon = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    // ---- 1: host_api_slow closes when the daemon is back under the floor -----------------------------------------------
    [Fact]
    public void Api_slow_alert_is_closed_when_the_daemon_is_fast_again()
    {
        var evaluator = new ApiLatencyEvaluator();
        var window = new List<TimeSpan>();
        var findings = new List<HostLoadFinding>();
        void Feed(int count, double ms)
        {
            for (var i = 0; i < count; i++)
            {
                window.Add(TimeSpan.FromMilliseconds(ms));
                var recent = window.Skip(Math.Max(0, window.Count - 120)).ToList();   // bounded, as the loop keeps it
                if (evaluator.Evaluate(Noon.AddMinutes(window.Count), "host-a", "Host-A", recent) is { } f) findings.Add(f);
            }
        }

        Feed(100, 100);     // healthy: 100 ms
        Feed(20, 5000);     // 20 minutes at 5 s: raised
        Feed(120, 100);     // two hours at 100 ms again

        Assert.Contains(findings, f => f.What == FindingKind.Raised);
        var cleared = Assert.Single(findings, f => f.What == FindingKind.Cleared);
        Assert.False(cleared.Unconfirmed);
        Assert.Empty(evaluator.OpenFindings());
    }

    /// <summary>Feeds (count, ms) phases one reading a minute through a bounded window; returns the findings.</summary>
    private static (List<HostLoadFinding> Findings, ApiLatencyEvaluator Evaluator) FeedLatency(params (int Count, double Ms)[] phases)
    {
        var evaluator = new ApiLatencyEvaluator();
        var window = new List<TimeSpan>();
        var findings = new List<HostLoadFinding>();
        foreach (var (count, ms) in phases)
            for (var i = 0; i < count; i++)
            {
                window.Add(TimeSpan.FromMilliseconds(ms));
                var recent = window.Skip(Math.Max(0, window.Count - 120)).ToList();
                if (evaluator.Evaluate(Noon.AddMinutes(window.Count), "host-a", "Host-A", recent) is { } f) findings.Add(f);
            }
        return (findings, evaluator);
    }

    [Fact]
    public void Api_slow_alert_clears_in_the_band_under_the_floor_when_the_host_is_at_its_usual_speed()
    {
        // A host whose usual speed is 230 ms (inside 0.8 × floor … floor): back at ratio ≈ 1, under the clear line (2),
        // the reading is judged and the alert closes.
        var (findings, evaluator) = FeedLatency((100, 230), (20, 5000), (120, 230));

        Assert.Single(findings, f => f.What == FindingKind.Raised);
        Assert.Single(findings, f => f.What == FindingKind.Cleared);
        Assert.Empty(evaluator.OpenFindings());
    }

    [Fact]
    public void Api_slow_alert_holds_in_the_band_under_the_floor_while_the_ratio_is_still_high()
    {
        // Usual speed 100 ms, now 230 ms: ratio ≈ 2.3, at or above the clear line, inside the floor's band — held: the
        // alert neither closes nor is restated, for half an hour (longer than the 5-minute clear duration).
        var (findings, evaluator) = FeedLatency((100, 100), (20, 5000), (30, 230));

        Assert.Single(findings, f => f.What == FindingKind.Raised);
        Assert.DoesNotContain(findings, f => f.What == FindingKind.Cleared);
        Assert.Single(evaluator.OpenFindings());
    }

    [Fact]
    public void Api_slow_alert_stays_open_while_the_daemon_is_still_slow()
    {
        // The other side of the fix: a reading over the floor is still judged by its ratio, and a slow daemon is not
        // closed just because readings keep coming.
        var evaluator = new ApiLatencyEvaluator();
        var window = new List<TimeSpan>();
        var findings = new List<HostLoadFinding>();
        for (var i = 0; i < 115; i++)
        {
            window.Add(TimeSpan.FromMilliseconds(i < 100 ? 100 : 5000));
            if (evaluator.Evaluate(Noon.AddMinutes(window.Count), "host-a", "Host-A", window.ToList()) is { } f) findings.Add(f);
        }

        Assert.Contains(findings, f => f.What == FindingKind.Raised);
        Assert.DoesNotContain(findings, f => f.What == FindingKind.Cleared);
        Assert.Single(evaluator.OpenFindings());
    }

    // ---- 2: a container still unreadable after its suspension ran out is suspended again -------------------------------
    [Fact]
    public async Task A_container_still_unreadable_after_its_suspension_is_suspended_again()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"t1-logmon-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        try
        {
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                db.Database.EnsureCreated();
                db.LogAlertRules.Add(new LogAlertRuleEntity { Name = "fatal", Pattern = "FATAL", Severity = "error" });
                db.SaveChanges();
            }
            var docker = new FakeDocker(new ContainerInfo
                { Id = "c-tunnel", Name = "ghostunnel", Image = "img:1", ServerId = "local", ServerName = "Host-A" })
                { FetchHangs = true };
            var notifications = new FakeNotifications();
            var monitor = new LogMonitorService(sp.GetRequiredService<IServiceScopeFactory>(), docker,
                new FakeServerConfig(new ServerConfig { Id = "local", Name = "Host-A", ConnectionType = ConnectionType.Local, IsDefault = true }),
                notifications, NullLogger<LogMonitorService>.Instance, TestBudget.Create(),
                new SelfMetricsImpl(), new NoExclusions(), new NoOutcomes(), TimeSpan.FromMilliseconds(50));

            for (var i = 0; i < 3; i++) await monitor.RunScanCycleAsync(CancellationToken.None);
            var first = Assert.Single(monitor.SuspendedContainers());

            // The five minutes pass (the service reads DateTime.UtcNow; the expiry is moved instead of waited for).
            var until = (ConcurrentDictionary<string, DateTime>)typeof(LogMonitorService)
                .GetField("_suspendedUntil", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor)!;
            foreach (var key in until.Keys.ToList()) until[key] = DateTime.UtcNow.AddSeconds(-1);

            // Still wedged: ten more cycles, every fetch it is asked for times out.
            for (var i = 0; i < 10; i++) await monitor.RunScanCycleAsync(CancellationToken.None);

            var again = Assert.Single(monitor.SuspendedContainers());
            Assert.True(again.Until - DateTime.UtcNow > TimeSpan.FromMinutes(10), "the second suspension takes the 15-minute step");
            Assert.Equal(2, notifications.Events.Count(e => e.EventType == NotificationEventTypes.LogScanSuspended));
            Assert.Equal("ghostunnel", first.ContainerName);
        }
        finally
        {
            sp.Dispose();
            TestSqlite.ClearPool(dbPath);
            try { File.Delete(dbPath); } catch { /* temp file */ }
        }
    }

    // ---- 3: cve_finding names its server and its container --------------------------------------------------------------
    private static NotificationEvent CveEvent(CveSource source)
    {
        var monitor = (CveMonitorService)RuntimeHelpers.GetUninitializedObject(typeof(CveMonitorService));
        var notifications = new FakeNotifications();
        var finding = new CveFinding { CveId = "CVE-2026-0001", Severity = CveSeverity.High, Package = "openssl" };
        var target = source == CveSource.Os
            ? new CveScanResult { ServerId = "host-b", Source = CveSource.Os, Findings = [finding] }
            : new CveScanResult
            {
                ServerId = "host-b", Source = CveSource.Container, ContainerId = "abc123", ContainerName = "web",
                Image = "nginx:1", Findings = [finding],
            };
        var send = typeof(CveMonitorService).GetMethod("TrySendAggregateNotificationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((Task)send.Invoke(monitor, [notifications, target, new List<CveFinding> { finding },
            new Dictionary<string, string> { ["host-b"] = "Host-B" }])!).GetAwaiter().GetResult();
        return Assert.Single(notifications.Events);
    }

    [Fact]
    public void Cve_notification_carries_its_server()
    {
        // CompositeNotificationService files `evt.ServerId ?? "local"` in AlertHistory.
        var evt = CveEvent(CveSource.Container);
        Assert.Equal("host-b", evt.ServerId);
        Assert.Equal("Host-B", evt.ServerName);
        Assert.Contains("Host-B", evt.ImageName);
    }

    [Fact]
    public void Cve_notification_of_a_container_is_looked_up_under_the_containers_name()
    {
        // CompositeNotificationService asks _prefs.ShouldNotify(evt.ContainerName, …); the mute is keyed by container name.
        Assert.Equal("web", CveEvent(CveSource.Container).ContainerName);
    }

    [Fact]
    public void Cve_notification_of_the_os_names_its_server()
    {
        var evt = CveEvent(CveSource.Os);
        Assert.Equal("host-b", evt.ServerId);
        Assert.Equal("OS on Host-B", evt.ContainerName);
    }

    // ---- 4: two single spikes are not a sustained breach --------------------------------------------------------------
    [Fact]
    public void Two_spikes_minutes_apart_are_not_a_sustained_breach()
    {
        var tracker = new BreachTracker(new HostLoadThresholds());
        var rules = BreachRules.Threshold(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), BreachRules.MinimumGapTolerance);
        double[] cpu = [95, 10, 10, 10, 95];     // one reading a minute; threshold 90, clear line 85
        var findings = new List<HostLoadFinding>();
        for (var i = 0; i < cpu.Length; i++)
            if (tracker.Consider(Noon.AddMinutes(i), "c-web", "web", NotificationEventTypes.HighCpu, cpu[i], 90,
                    (v, open) => $"web: CPU at {v:F0}%.", v => $"web: CPU back to {v:F0}%.", rules) is { } f)
                findings.Add(f);
        Assert.Empty(findings);
    }

    [Fact]
    public void A_reading_inside_the_hysteresis_band_still_continues_a_breach()
    {
        // Unchanged by the fix: the band (85–90) neither resets nor clears, so a breach hovering at the line is raised.
        var tracker = new BreachTracker(new HostLoadThresholds());
        var rules = BreachRules.Threshold(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), BreachRules.MinimumGapTolerance);
        double[] cpu = [95, 88, 88, 95];
        HostLoadFinding? raised = null;
        for (var i = 0; i < cpu.Length; i++)
            raised ??= tracker.Consider(Noon.AddMinutes(i), "c-web", "web", NotificationEventTypes.HighCpu, cpu[i], 90,
                (v, open) => $"web: CPU at {v:F0}%.", v => $"web: CPU back to {v:F0}%.", rules);
        Assert.Equal(FindingKind.Raised, raised?.What);
    }

    [Fact]
    public void A_disk_that_flaps_between_failing_and_healthy_still_raises()
    {
        // Disk health keeps the confirmation through good readings (UnbrokenConfirmation: false): a flapping SMART item is
        // a failing one, and one good reading between bad ones must not keep it quiet for ever.
        var alerts = new Whiskers.Services.Metrics.DiskHealth.DiskHealthAlerts();
        var findings = new List<HostLoadFinding>();
        Whiskers.Services.Metrics.DiskHealth.DiskHealthReading? previous = null;
        for (var m = 0; m <= 12; m++)
        {
            var state = m % 2 == 0
                ? Whiskers.Services.Metrics.DiskHealth.DiskHealthState.DegradedOrFailing
                : Whiskers.Services.Metrics.DiskHealth.DiskHealthState.Healthy;
            var at = Noon.AddMinutes(m);
            var reading = new Whiskers.Services.Metrics.DiskHealth.DiskHealthReading("host-a", state,
                [new Whiskers.Services.Metrics.DiskHealth.DiskHealthItem(Whiskers.Services.Metrics.DiskHealth.DiskHealthKinds.Smart,
                    "/dev/sda", state, "SMART overall-health", at)], at);
            findings.AddRange(alerts.Evaluate("host-a", "Host-A", reading, previous, at));
            previous = reading;
        }
        Assert.Contains(findings, f => f.Kind == NotificationEventTypes.DiskFailing && f.What == FindingKind.Raised);
    }

    // ---- 5: an AI trigger's cooldown is per trigger and server ----------------------------------------------------------
    private sealed class OneTrigger(AiTrigger trigger) : IAiTriggerStore
    {
        public IReadOnlyList<AiTrigger> Triggers { get; } = [trigger];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long Revision => 1;
        public Task SaveAsync(List<AiTrigger> triggers, Whiskers.Models.Agent.AgentPrincipal editor, long readAtRevision) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class RunCounter : IServiceProvider
    {
        public int Runs;
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(Whiskers.Services.Agent.IAgentService)) Interlocked.Increment(ref Runs);
            return null;   // the run ends there (caught by the dispatcher); it was started, which is what counts
        }
    }

    [Fact]
    public async Task A_trigger_runs_for_the_same_event_on_a_second_server_but_not_twice_on_one()
    {
        var runs = new RunCounter();
        var dispatcher = new AiTriggerDispatcher(
            new OneTrigger(new AiTrigger { Name = "host load", EventTypes = ["host_cpu_high"], CooldownSeconds = 300 }), runs,
            new StaticOptionsMonitor<Whiskers.Configuration.AgentSettings>(new() { Enabled = true }));
        NotificationEvent HostCpu(string id, string name) => new()
            { EventType = NotificationEventTypes.HostCpuHigh, ServerId = id, ServerName = name, ContainerName = name };

        await dispatcher.OnEventAsync(HostCpu("host-a", "Host-A"));
        await dispatcher.OnEventAsync(HostCpu("host-b", "Host-B"));
        await dispatcher.OnEventAsync(HostCpu("host-a", "Host-A"));   // inside Host-A's cooldown
        await Task.Delay(200);

        Assert.Equal(2, runs.Runs);
    }

    // ---- 6: a host without apt is not scannable, not clean --------------------------------------------------------------
    private sealed class HostWithoutApt : Whiskers.Services.Server.IHostCommandExecutor
    {
        public Task<Whiskers.Services.Server.CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null) =>
            // What sh answers on Alpine/RHEL: "… 2>&1 | tail -3" exits with tail's 0 and prints the shell's error;
            // "command -v apt" fails, so nothing is echoed; "apt list … | grep … || true" exits 0 with no output.
            Task.FromResult(new Whiskers.Services.Server.CommandResult
            {
                ExitCode = 0,
                Output = command.Contains("apt-get update", StringComparison.Ordinal) ? "sudo: apt-get: command not found\n" : "",
            });
    }

    private sealed class CleanAptHost : Whiskers.Services.Server.IHostCommandExecutor
    {
        public Task<Whiskers.Services.Server.CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null) =>
            // apt exists (the marker is echoed) and nothing is pending.
            Task.FromResult(new Whiskers.Services.Server.CommandResult
            {
                ExitCode = 0,
                Output = command.Contains("command -v apt", StringComparison.Ordinal) ? OsCveScanner.AptPresentMarker + "\n" : "",
            });
    }

    [Fact]
    public async Task An_os_scan_of_a_host_without_apt_does_not_read_as_clean()
    {
        var scanner = new OsCveScanner(new HostWithoutApt(),
            new StaticOptionsMonitor<Whiskers.Configuration.CveMonitorSettings>(new()), NullLogger<OsCveScanner>.Instance);
        var result = await scanner.ScanAsync("alpine-host");
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("Not scannable", result.Error);
        Assert.False(CveScanOutcome.ShouldNotify(result));
    }

    [Fact]
    public async Task A_host_stored_as_clean_reads_not_scannable_once_apt_is_gone()
    {
        // An earlier successful scan stored "clean" (no error, no findings). The failure replaces it; only real findings
        // are kept over a failed scan.
        var storedClean = new CveScanResult { ServerId = "alpine-host", Source = CveSource.Os };
        var scanner = new OsCveScanner(new HostWithoutApt(),
            new StaticOptionsMonitor<Whiskers.Configuration.CveMonitorSettings>(new()), NullLogger<OsCveScanner>.Instance);
        var result = await scanner.ScanAsync("alpine-host");

        Assert.Equal(ScanResultAction.Store, CveScanOutcome.Decide(storedClean, result));
        var withFindings = new CveScanResult
        {
            ServerId = "alpine-host", Source = CveSource.Os,
            Findings = [new CveFinding { CveId = "CVE-2026-0001", Severity = CveSeverity.High, Package = "openssl" }],
        };
        Assert.Equal(ScanResultAction.KeepPrevious, CveScanOutcome.Decide(withFindings, result));
    }

    [Fact]
    public async Task An_os_scan_of_a_clean_apt_host_is_clean()
    {
        var scanner = new OsCveScanner(new CleanAptHost(),
            new StaticOptionsMonitor<Whiskers.Configuration.CveMonitorSettings>(new()), NullLogger<OsCveScanner>.Instance);
        var result = await scanner.ScanAsync("debian-host");
        Assert.Null(result.Error);
        Assert.Empty(result.Findings);
    }

    // ---- 7: an action's outcome is judged on a reading from the end of its window ---------------------------------------
    private static async Task<ActionVerdict> JudgeAsync(AutomaticActionKind kind, TimeSpan judgedAfter,
        Action<SelfMetricsImpl> before, Action<MetricsDbContext, DateTime>? after = null, int? collectionSeconds = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"t1-outcomes-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={path}"));
        var sp = services.BuildServiceProvider();
        try
        {
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
            var self = new SelfMetricsImpl();
            before(self);
            var collection = collectionSeconds is { } seconds
                ? Whiskers.Configuration.IntervalValidation.MetricsCollection(
                    new StaticOptionsMonitor<Whiskers.Configuration.MetricsSettings>(new() { CollectionIntervalSeconds = seconds }),
                    NullLogger.Instance)
                : null;
            var service = new ActionOutcomeService(sp.GetRequiredService<IServiceScopeFactory>(), self,
                NullLogger<ActionOutcomeService>.Instance, collection);
            await service.RecordAsync(kind, "srv", "c1", "container-1");
            DateTime executed;
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                executed = (await db.ActionOutcomes.SingleAsync()).ExecutedAtUtc;
                after?.Invoke(db, executed);
                await db.SaveChangesAsync();
            }
            return Assert.Single(await service.EvaluateDueAsync(executed + judgedAfter)).Verdict;
        }
        finally
        {
            sp.Dispose();
            TestSqlite.ClearPool(path);
            try { File.Delete(path); } catch { /* temp file */ }
        }
    }

    [Fact]
    public async Task A_restarted_container_that_died_seconds_later_is_not_judged_as_having_worked()
    {
        // One sample ten seconds after the restart, then nothing: the container was gone for the rest of the window.
        var verdict = await JudgeAsync(AutomaticActionKind.ContainerRestart, TimeSpan.FromMinutes(6), _ => { },
            (db, executed) => db.ContainerMetrics.Add(new ContainerMetricEntity
                { ContainerId = "c1", ServerId = "srv", Timestamp = executed.AddSeconds(10) }));
        Assert.Equal(ActionVerdict.NotMeasurable, verdict);
    }

    [Fact]
    public async Task A_restarted_container_still_running_at_the_end_of_its_window_worked()
    {
        var verdict = await JudgeAsync(AutomaticActionKind.ContainerRestart, TimeSpan.FromMinutes(6), _ => { },
            (db, executed) => db.ContainerMetrics.Add(new ContainerMetricEntity
                { ContainerId = "c1", ServerId = "srv", Timestamp = executed.AddMinutes(5).AddSeconds(-20) }));
        Assert.Equal(ActionVerdict.Worked, verdict);
    }

    [Fact]
    public async Task With_a_five_minute_collection_interval_a_restart_that_worked_is_judged_as_worked()
    {
        // Collected every 5 minutes, the newest row can lie up to 5 minutes before the window closes: the tolerance is
        // two intervals (10 minutes), not the 2-minute minimum.
        var verdict = await JudgeAsync(AutomaticActionKind.ContainerRestart, TimeSpan.FromMinutes(6), _ => { },
            (db, executed) => db.ContainerMetrics.Add(new ContainerMetricEntity
                { ContainerId = "c1", ServerId = "srv", Timestamp = executed.AddMinutes(1) }),
            collectionSeconds: 300);
        Assert.Equal(ActionVerdict.Worked, verdict);
    }

    [Fact]
    public async Task A_container_of_the_same_id_on_another_server_says_nothing_about_this_one()
    {
        var verdict = await JudgeAsync(AutomaticActionKind.ContainerRestart, TimeSpan.FromMinutes(6), _ => { },
            (db, executed) => db.ContainerMetrics.Add(new ContainerMetricEntity
                { ContainerId = "c1", ServerId = "other", Timestamp = executed.AddMinutes(5) }));
        Assert.Equal(ActionVerdict.NotMeasurable, verdict);
    }

    [Fact]
    public async Task A_self_throttle_is_not_judged_on_a_latency_measured_before_it()
    {
        // The only latency on record is from before the throttle; nothing was measured afterwards.
        var verdict = await JudgeAsync(AutomaticActionKind.SelfThrottle, TimeSpan.FromMinutes(16),
            self => self.RecordApiCall("srv", TimeSpan.FromMilliseconds(100)));
        Assert.Equal(ActionVerdict.NotMeasurable, verdict);
    }

    // ---- 8: a removed server is not reported as stalled ----------------------------------------------------------------
    [Fact]
    public async Task A_removed_server_is_not_reported_as_stalled()
    {
        var metrics = new SelfMetricsImpl();
        var sent = new FakeNotifications();
        var supervisor = new ScanSupervisor(metrics, sent,
            new FakeServerConfig(new ServerConfig { Id = "host-a", Name = "Host-A", IsDefault = true }),
            NullLogger<ScanSupervisor>.Instance, TimeSpan.Zero);
        // "old-host" was removed; its loop ran until then (or was restored from the stored samples) and never runs again.
        metrics.Restore("logmonitor", "old-host", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));
        // A configured server and a fleet-wide loop stalled the same way are still reported.
        metrics.Restore("logmonitor", "host-a", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));
        metrics.Restore("scheduler", "*", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));

        await supervisor.CheckAsync();

        var stalled = sent.Events.Where(e => e.EventType == NotificationEventTypes.MonitoringStalled)
            .Select(e => e.ServerId).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(["*", "host-a"], stalled);
    }

    // ---- Contract 0017, amendment 4 (G2): two loops stalling on one server are two alerts in every channel -------------
    [Fact]
    public async Task Two_loops_stalled_on_one_server_carry_their_loop_as_throttle_subject()
    {
        var metrics = new SelfMetricsImpl();
        var sent = new FakeNotifications();
        var supervisor = new ScanSupervisor(metrics, sent,
            new FakeServerConfig(new ServerConfig { Id = "web", Name = "Web", IsDefault = true }),
            NullLogger<ScanSupervisor>.Instance, TimeSpan.Zero);
        metrics.Restore("logmonitor", "web", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));
        metrics.Restore("cvescan", "web", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(1));

        await supervisor.CheckAsync();

        var stalled = sent.Events.Where(e => e.EventType == NotificationEventTypes.MonitoringStalled).ToList();
        Assert.Equal([NotificationThrottleSubjects.Loop("cvescan"), NotificationThrottleSubjects.Loop("logmonitor")],
            stalled.Select(e => e.ThrottleSubject).OrderBy(s => s, StringComparer.Ordinal));
        // Through one channel's throttle, as the channels see them: both are delivered.
        var throttler = new NotificationThrottler();
        foreach (var evt in stalled)
        {
            Assert.False(throttler.IsThrottled(evt, 5), $"{evt.ThrottleSubject} swallowed by another loop's slot");
            throttler.Record(evt, 5);
        }
    }

    // ---- 9: max-size=-1 is json-file's "unlimited", not a rotation limit -----------------------------------------------
    [Theory]
    [InlineData("-1", false)]
    [InlineData(" -1 ", false)]
    [InlineData(null, false)]
    [InlineData("10m", true)]
    public void A_max_size_of_minus_one_is_not_a_rotation_limit(string? maxSize, bool rotated)
    {
        var config = new ContainerLogConfiguration("json-file", maxSize, null, "/var/lib/docker/containers/c1/c1-json.log");
        Assert.Equal(rotated, config.RotationConfigured);
    }
}
