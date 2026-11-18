using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Observability;
using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// The supervisor that reports the absence of reports (Plan-0002 WP5.3).
///
/// <para>Every other guard says "here is a problem I found". This one says "nothing has been checked here for
/// a while" — the statement nobody was making on 2026-08-26 while a host sat at 98% CPU for six days. Its
/// value is that it does not care <em>why</em>: a wedged socket, a suspended container, a paused loop, an
/// uncaught exception and a dead thread all look the same from here, and all mean the same thing.</para>
///
/// <para>Tests that only assert silence would pass against a supervisor that never fires at all, so the first
/// test here produces a real stall and demands the alert.</para>
/// </summary>
public class ScanSupervisorTests
{
    // Short enough to let a genuine stall happen inside a test, by removing the production floor of 5 minutes.
    private static readonly TimeSpan TinyInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan NoFloor = TimeSpan.Zero;

    private static (ScanSupervisor Supervisor, SelfMetrics Metrics, FakeNotifications Sent) Build(TimeSpan? floor = null)
    {
        var metrics = new SelfMetrics();
        var sent = new FakeNotifications();
        var supervisor = new ScanSupervisor(
            metrics, sent,
            new FakeServerConfig(new ServerConfig { Id = "host-a", Name = "Host-A", IsDefault = true }),
            NullLogger<ScanSupervisor>.Instance,
            floor ?? NoFloor);

        return (supervisor, metrics, sent);
    }

    [Fact]
    public async Task A_loop_that_runs_on_time_and_fails_every_cycle_is_reported()
    {
        // The supervisor used to fall back to the last ATTEMPT when there was no success, while its own
        // comment claimed the opposite. A loop failing every cycle therefore kept resetting its age to zero
        // and stayed silent forever — which is exactly the 2026-08-26 shape: the loop ran on time, achieved
        // nothing, and nobody noticed for six days.
        var (supervisor, metrics, sent) = Build();

        for (var i = 0; i < 5; i++)
            metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: false, interval: TinyInterval);

        await supervisor.CheckAsync();

        Assert.Equal("monitoring_stalled", Assert.Single(sent.Events).EventType);
    }

    [Fact]
    public async Task A_loop_that_has_only_just_started_is_given_a_few_cycles()
    {
        // The other half. Reporting a loop on its first failed cycle would turn every restart into an
        // incident, and an alarm that fires on every deploy is one people mute.
        var (supervisor, metrics, sent) = Build();
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: false, interval: TinyInterval);

        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task A_loop_that_has_stopped_is_reported_once_and_its_return_is_reported_too()
    {
        var (supervisor, metrics, sent) = Build();
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: true, interval: TinyInterval);

        // Nothing has gone wrong yet.
        await supervisor.CheckAsync();
        Assert.Empty(sent.Events);

        // Now the loop simply stops recording — exactly what a stalled loop looks like from the outside.
        await Task.Delay(TinyInterval * 5);
        await supervisor.CheckAsync();

        var alarm = Assert.Single(sent.Events);
        Assert.Equal("monitoring_stalled", alarm.EventType);
        Assert.Equal("host-a", alarm.ServerId);

        // A lasting stall is one alert, not one per check — an alert channel that repeats gets muted, and a
        // muted channel is the same blindness by another route.
        await supervisor.CheckAsync();
        await supervisor.CheckAsync();
        Assert.Single(sent.Events);

        // And when it runs again, that has to be said too: otherwise the operator is left believing the
        // server is still unmonitored.
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: true, interval: TinyInterval);
        await supervisor.CheckAsync();

        Assert.Equal(
            new[] { "monitoring_stalled", "monitoring_resumed" },
            sent.Events.Select(e => e.EventType));
    }

    [Fact]
    public async Task A_loop_that_keeps_running_is_never_reported()
    {
        var (supervisor, metrics, sent) = Build();

        for (var i = 0; i < 5; i++)
        {
            metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: true, interval: TinyInterval);
            await supervisor.CheckAsync();
            await Task.Delay(TinyInterval);
        }

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task A_failing_loop_is_still_a_running_loop_until_the_allowance_runs_out()
    {
        // Failures are not stalls. A loop that reports failures is alive and its own guards are handling
        // them; this supervisor exists for the case where nothing is reported at all.
        var (supervisor, metrics, sent) = Build();
        metrics.RecordCycle("health", "host-a", TimeSpan.FromMilliseconds(1), success: false, interval: TinyInterval);

        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task A_deliberately_skipped_server_is_never_called_stalled()
    {
        // "This loop does not cover that server" is already stated by the skip. Reporting it as a stall too
        // would bury the real cases under noise from every Kubernetes host in the fleet.
        var (supervisor, metrics, sent) = Build();
        metrics.RecordSkip("cve", "host-a", "Kubernetes server — this loop only speaks to Docker hosts");

        await Task.Delay(TinyInterval * 5);
        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task A_loop_without_a_declared_cadence_is_left_alone()
    {
        // Without knowing how often a loop intends to run, "no cycle for ten minutes" means nothing — one
        // loop runs every minute, another every six hours. Silence is the honest answer; a guessed threshold
        // would turn this supervisor into a noise source and get it ignored.
        var (supervisor, metrics, sent) = Build();
        metrics.RecordCycle("mystery", "host-a", TimeSpan.FromMilliseconds(1), success: true);

        await Task.Delay(TinyInterval * 5);
        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task The_production_floor_keeps_a_fast_loop_from_crying_wolf()
    {
        // With the real 5-minute floor, a loop running every 30 ms cannot raise an alarm after 150 ms.
        var (supervisor, metrics, sent) = Build(floor: TimeSpan.FromMinutes(5));
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(1), success: true, interval: TinyInterval);

        await Task.Delay(TinyInterval * 5);
        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }
}
