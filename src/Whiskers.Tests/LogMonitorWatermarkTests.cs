using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// The watermark ratchet from the 2026-08-26 incident (Plan-0002 WP1/WP2).
///
/// <para>The watermark was only written after a <em>successful</em> fetch. So on failure <c>since</c> stayed
/// put while <c>now</c> moved on, and the requested window grew by one cycle interval every time. The comment
/// in the code claimed "the next cycle retries the same window"; it was not the same window, and that
/// difference is the whole mechanism: failure → wider window → more expensive fetch → certain failure. Six
/// days without a single recovery.</para>
///
/// <para>Applying <c>since</c> costs dockerd the whole file — it decodes the JSON log from the start to find
/// the cut-off — so a wider window is genuinely more work even when it yields nothing.</para>
/// </summary>
public sealed class LogMonitorWatermarkTests : IDisposable
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"logmon-wm-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public LogMonitorWatermarkTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Database.EnsureCreated();
        db.LogAlertRules.Add(new LogAlertRuleEntity { Name = "fatal", Pattern = "FATAL", Severity = "error" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }

    private LogMonitorService Monitor(FakeDocker docker, FakeNotifications notifications) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }),
            notifications,
            NullLogger<LogMonitorService>.Instance,
            TestBudget.Create(), new Whiskers.Services.Observability.SelfMetrics.SelfMetrics(),
            new NoExclusions(), new NoOutcomes(),
            FetchTimeout);

    private static FakeDocker WedgedContainer() =>
        new(new ContainerInfo { Id = "c-tunnel", Name = "ghostunnel", Image = "img:1", ServerId = "local", ServerName = "Badwolf" })
        {
            // NOT a long delay. "Eight times the timeout" reads decisive and is not: under thread-pool
            // starvation the cancellation timer lands late and a 400 ms fetch completes — measured at 16 of 60
            // attempts. A completed fetch clears the consecutive-timeout run, so one such cycle silently reset
            // the three-strike counter and the lockout never fired. Hanging until cancelled has no timer to
            // lose the race with.
            FetchHangs = true
        };

    [Fact]
    public async Task A_failed_fetch_does_not_make_the_next_one_more_expensive()
    {
        // The container must SUCCEED once before it goes slow. That is the shape of the incident — a healthy
        // container that degraded — and it is also the only shape in which the ratchet exists at all: without
        // a successful fetch there is no watermark to leave behind, so `since` falls back to "now" every cycle
        // and the window stays flat whether the bug is present or not. An earlier version of this test skipped
        // the healthy cycle and therefore passed against the unfixed code, proving nothing.
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-tunnel", Name = "ghostunnel", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });
        var monitor = Monitor(docker, new FakeNotifications());

        await monitor.RunScanCycleAsync(CancellationToken.None);   // healthy: sets the watermark
        docker.FetchHangs = true;                                  // and now it wedges — see WedgedContainer()

        // The gap between cycles has to be large enough that the ratchet would actually show. With a short
        // gap the window widens by that gap per cycle and any generous tolerance swallows it — an earlier
        // version used 120 ms and could not tell the two apart either. The real cycle interval is 60 s.
        const int gapMs = 400;

        // Each window is judged against the time that ACTUALLY passed before it, not against the other
        // windows. An earlier version compared them to each other and so assumed every gap was the same
        // length; on a loaded CI runner one Task.Delay overshot by a second, the window correctly grew with
        // it, and the test called a healthy run a ratchet. What the fix promises is not "a constant window"
        // but "a window no wider than the time since the last attempt" — that is the property to assert, and
        // it holds however badly the machine stutters.
        var measured = new List<(TimeSpan Window, TimeSpan Elapsed)>();
        var previousCycleAt = DateTime.UtcNow;

        var seenCalls = docker.Calls.Count;
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(gapMs);
            var at = DateTime.UtcNow;
            await monitor.RunScanCycleAsync(CancellationToken.None);

            // Only measure cycles that actually issued a fetch. After the suspension kicks in there is no new
            // call, and reusing the previous one against a later clock would invent a growing window that the
            // code never asked for — the first version of this test did exactly that.
            if (docker.Calls.Count > seenCalls)
            {
                seenCalls = docker.Calls.Count;
                if (docker.CallsInOrder[^1].Since is { } since)
                    measured.Add((at - since, at - previousCycleAt));
                previousCycleAt = at;
            }
        }

        Assert.True(measured.Count >= 3, $"expected at least 3 failing fetches, got {measured.Count}");

        // The failed fetch advances the watermark, so the next window spans one cycle gap — never the whole
        // stretch back to the last SUCCESS. Growing past the gap means the watermark is being left behind on
        // failure: the ratchet that turned a slow container into a permanently failing one.
        var tolerance = TimeSpan.FromMilliseconds(150);
        foreach (var (window, elapsed) in measured)
            Assert.True(window <= elapsed + tolerance,
                $"the fetch asked for {window.TotalMilliseconds:F0}ms of logs after only " +
                $"{elapsed.TotalMilliseconds:F0}ms had passed since the previous attempt — the watermark is " +
                "being left behind on failure. The ratchet is still there. All windows: " +
                $"[{string.Join(", ", measured.Select(m => $"{m.Window.TotalMilliseconds:F0}ms/{m.Elapsed.TotalMilliseconds:F0}ms"))}]");
    }

    [Fact]
    public async Task The_requested_window_never_exceeds_the_cap()
    {
        var docker = WedgedContainer();
        var monitor = Monitor(docker, new FakeNotifications());

        for (var i = 0; i < 5; i++)
        {
            await monitor.RunScanCycleAsync(CancellationToken.None);
            await Task.Delay(120);
        }

        // Whatever happens, a single fetch may never ask for more than the cap. Losing the lines in a longer
        // outage is the deliberate side of the trade: today they are lost anyway, just permanently.
        var widest = docker.CallsInOrder
            .Where(c => c.Since is not null)
            .Select(c => DateTime.UtcNow - c.Since!.Value)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();

        Assert.True(widest <= LogMonitorService.MaxLookback + TimeSpan.FromSeconds(2),
            $"widest requested window was {widest.TotalMinutes:F1} min, cap is {LogMonitorService.MaxLookback.TotalMinutes:F0} min");
    }

    [Fact]
    public async Task A_container_whose_logs_stay_unreadable_is_reported_within_three_cycles()
    {
        // The earliest and most precise signal in the whole incident: the fetch timeouts were already being
        // logged, and nobody counted them. Counted, this fires after three minutes instead of six days.
        var docker = WedgedContainer();
        var notifications = new FakeNotifications();
        var monitor = Monitor(docker, notifications);

        for (var i = 0; i < 3; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        var alert = Assert.Single(notifications.Events);
        Assert.Equal("log_scan_suspended", alert.EventType);
        Assert.Equal("ghostunnel", alert.ContainerName);
    }

    [Fact]
    public async Task A_suspended_container_can_be_listed_and_names_itself()
    {
        // Plan-0002 WP5. The suspension was announced once, and then the state lived only inside the scanner.
        // A one-time alert scrolls out of the channel; a container nobody is reading looks exactly like one
        // with nothing to report. So it has to be visible somewhere that is still true tomorrow — and it has
        // to say WHICH container, because "local:c-tunnel" in a UI is a riddle, not a report.
        var monitor = Monitor(WedgedContainer(), new FakeNotifications());

        for (var i = 0; i < 3; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        var suspended = Assert.Single(monitor.SuspendedContainers());
        Assert.Equal("ghostunnel", suspended.ContainerName);
        Assert.Equal("local", suspended.ServerId);
        Assert.True(suspended.ConsecutiveTimeouts >= 3);
        Assert.True(suspended.Until > DateTime.UtcNow);
    }

    [Fact]
    public async Task A_container_that_is_being_read_normally_is_not_listed_as_suspended()
    {
        // The other direction: if everything appeared here, the list would say nothing. A view that always
        // shows something is a view people stop reading.
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-ok", Name = "burg-web", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });
        var monitor = Monitor(docker, new FakeNotifications());

        for (var i = 0; i < 5; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        Assert.Empty(monitor.SuspendedContainers());
    }

    [Fact]
    public async Task A_healthy_container_is_never_suspended()
    {
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-ok", Name = "burg-web", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });
        var notifications = new FakeNotifications();
        var monitor = Monitor(docker, notifications);

        for (var i = 0; i < 5; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(notifications.Events, e => e.EventType == "log_scan_suspended");
    }

    [Fact]
    public async Task A_removed_container_is_not_reported_as_a_scan_problem()
    {
        // "Container is gone" and "container's logs are unreadable" are different facts. Counting the first
        // as the second would page someone every time a container is legitimately removed.
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-gone", Name = "old-job", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });
        docker.FailingServerIds.Add("local");   // fetch throws immediately, no timeout involved
        var notifications = new FakeNotifications();
        var monitor = Monitor(docker, notifications);

        for (var i = 0; i < 5; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(notifications.Events, e => e.EventType == "log_scan_suspended");
    }

    [Fact]
    public async Task The_wedged_container_never_answers_however_the_machine_is_behaving()
    {
        // Guards the fix for two CI flakes in one day (2026-08-27). The wedged container used to be modelled
        // as a 400 ms delay against a 50 ms timeout, which reads decisive and is a race: with the thread pool
        // hogged, 16 of 60 such fetches ran to completion. A completed fetch calls NoteReadable, which CLEARS
        // the consecutive-timeout run — so one unlucky cycle reset the three-strike counter and the lockout
        // silently never fired.
        //
        // Every suspension test here does exactly three cycles for a three-strike rule, so there is no margin
        // to absorb that. Rather than widen the margin — which would have hidden a real lockout failure just
        // as effectively — the fake now hangs until cancelled. This test is what stops it drifting back.
        var docker = WedgedContainer();
        var monitor = Monitor(docker, new FakeNotifications());

        for (var i = 0; i < 3; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);

        Assert.True(docker.Calls.Count >= 3, $"expected at least 3 fetch attempts, saw {docker.Calls.Count}");
        Assert.Equal(0, docker.CompletedFetches);
        Assert.NotEmpty(monitor.SuspendedContainers());
    }
}
