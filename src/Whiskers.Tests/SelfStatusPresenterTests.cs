using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;
using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// The judgement behind the self-status view (Plan-0003 WP4).
///
/// <para>Deciding that a loop has stalled is a judgement, not formatting, so it lives outside the page and is
/// tested here. It uses the same three-interval rule as <see cref="ScanSupervisor"/> and the MCP tool: a view
/// with its own quietly different threshold would be worse than no view, because it would look authoritative
/// while contradicting the alert that woke someone up.</para>
/// </summary>
public class SelfStatusPresenterTests
{
    private static readonly ServerConfig HostA = new()
    {
        Id = "host-a", Name = "Host-A", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local
    };

    private static string Name(string id) => id == "host-a" ? "Host-A" : id;

    private static LoopHealth Loop(
        TimeSpan? sinceSuccess, TimeSpan? interval, string? skipReason = null, string name = "logmonitor",
        long cycles = 10, long failures = 0)
    {
        var now = DateTime.UtcNow;
        return new LoopHealth(
            name, "host-a",
            sinceSuccess is null ? null : now - sinceSuccess.Value,
            now, TimeSpan.FromMilliseconds(120), cycles, failures, Skips: 0, skipReason, interval);
    }

    [Fact]
    public void A_loop_past_three_of_its_own_intervals_is_called_stalled()
    {
        // The assertion that matters: this has to FIRE. A presenter that never says "stalled" would pass every
        // other test in this file.
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: TimeSpan.FromMinutes(4), interval: TimeSpan.FromMinutes(1)) },
            DateTime.UtcNow, Name);

        Assert.Equal(LoopVerdict.Stalled, Assert.Single(rows).Verdict);
    }

    [Fact]
    public void Just_inside_three_intervals_is_still_healthy()
    {
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: TimeSpan.FromMinutes(2), interval: TimeSpan.FromMinutes(1)) },
            DateTime.UtcNow, Name);

        Assert.Equal(LoopVerdict.Healthy, Assert.Single(rows).Verdict);
    }

    [Fact]
    public void The_threshold_follows_the_loops_own_cadence_and_not_a_fixed_clock()
    {
        // Two hours is nothing for a six-hourly CVE scan and a catastrophe for a one-minute log scan. A fixed
        // wall-clock threshold would be wrong for one of them no matter which number was picked.
        var slow = Loop(sinceSuccess: TimeSpan.FromHours(2), interval: TimeSpan.FromHours(6), name: "cve");
        var fast = Loop(sinceSuccess: TimeSpan.FromHours(2), interval: TimeSpan.FromMinutes(1));

        var rows = SelfStatusPresenter.LoopRows(new[] { slow, fast }, DateTime.UtcNow, Name);

        Assert.Equal(LoopVerdict.Healthy, rows.Single(r => r.Loop == "cve").Verdict);
        Assert.Equal(LoopVerdict.Stalled, rows.Single(r => r.Loop == "logmonitor").Verdict);
    }

    [Fact]
    public void A_loop_that_runs_on_time_and_never_succeeds_is_still_called_stalled()
    {
        // The gap this test was written to expose, and it did. The verdict used to fall back to the last
        // ATTEMPT when there was no success — so a loop failing every single cycle kept resetting its own age
        // to zero and looked permanently healthy. That is exactly the 2026-08-26 shape: the thing that ran on
        // time and achieved nothing was the thing nobody noticed.
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: null, interval: TimeSpan.FromMinutes(1), cycles: 200, failures: 200) },
            DateTime.UtcNow, Name);

        var row = Assert.Single(rows);
        Assert.Equal(LoopVerdict.Stalled, row.Verdict);
        Assert.Null(row.Age);
        Assert.Equal("never", SelfStatusPresenter.Age(row.Age));
    }

    [Fact]
    public void A_freshly_started_loop_gets_a_few_cycles_before_being_called_broken()
    {
        // The other side of the same rule. Calling a loop broken on its first cycle would make every restart
        // an incident, and an alarm that fires on every deploy is one people mute.
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: null, interval: TimeSpan.FromMinutes(1), cycles: 1, failures: 1) },
            DateTime.UtcNow, Name);

        Assert.Equal(LoopVerdict.Healthy, Assert.Single(rows).Verdict);
    }

    [Fact]
    public void A_skipped_server_is_its_own_state_and_not_a_fault()
    {
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: TimeSpan.FromDays(3), interval: TimeSpan.FromMinutes(1), skipReason: "kubernetes") },
            DateTime.UtcNow, Name);

        var row = Assert.Single(rows);
        Assert.Equal(LoopVerdict.Skipped, row.Verdict);
        Assert.Equal("kubernetes", row.SkipReason);
    }

    [Fact]
    public void Without_a_declared_cadence_no_verdict_is_offered()
    {
        var rows = SelfStatusPresenter.LoopRows(
            new[] { Loop(sinceSuccess: TimeSpan.FromDays(3), interval: null) }, DateTime.UtcNow, Name);

        Assert.Equal(LoopVerdict.Unjudged, Assert.Single(rows).Verdict);
    }

    [Fact]
    public void Stalled_loops_come_first()
    {
        // The page exists to answer "is anything wrong?" without reading all of it.
        var rows = SelfStatusPresenter.LoopRows(new[]
        {
            Loop(sinceSuccess: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMinutes(1), name: "aaa-healthy"),
            Loop(sinceSuccess: TimeSpan.FromHours(2), interval: TimeSpan.FromMinutes(1), name: "zzz-broken")
        }, DateTime.UtcNow, Name);

        Assert.Equal("zzz-broken", rows[0].Loop);
    }

    [Fact]
    public void The_view_reads_the_supervisors_threshold_rather_than_keeping_its_own()
    {
        // Not a style point. Two constants meaning the same thing drift, and the day they do, the page says
        // "fine" about the very loop that just paged someone — after which the next person decides the
        // alerting is broken rather than the loop. The presenter reads the supervisor's value, so this holds
        // by construction; the test is here so a future edit that reintroduces a literal 3 is caught.
        Assert.Equal(ScanSupervisor.IntervalsBeforeAlarm, SelfStatusPresenter.IntervalsBeforeStalled);
    }

    [Fact]
    public void An_age_is_shown_as_an_age_and_not_as_a_timestamp()
    {
        // "14:02" makes the reader do the subtraction, and doing it wrong is exactly how a six-day-old
        // failure goes unnoticed.
        Assert.Equal("45s ago", SelfStatusPresenter.Age(TimeSpan.FromSeconds(45)));
        Assert.Equal("5m ago", SelfStatusPresenter.Age(TimeSpan.FromMinutes(5)));
        Assert.Equal("2.5h ago", SelfStatusPresenter.Age(TimeSpan.FromHours(2.5)));
        Assert.Equal("6d ago", SelfStatusPresenter.Age(TimeSpan.FromDays(6)));
    }

    [Fact]
    public void A_container_the_scan_gave_up_on_and_one_it_excludes_are_never_given_the_same_label()
    {
        // Both look identical from outside — no findings — and mean opposite things. A shared label would
        // make an operator read a fault as a decision, which is how a broken log fetch survives a review.
        var suspended = new[]
        {
            new Whiskers.Services.LogMonitor.SuspendedContainer(
                "host-a", "c1", "web", DateTime.UtcNow.AddMinutes(15), ConsecutiveTimeouts: 4)
        };
        var excluded = new[]
        {
            new Whiskers.Services.LogMonitor.Hygiene.LogScanExclusion(
                "host-a", "c2", "ghostunnel", "access-path", "Whiskers reaches Docker through this container.")
        };

        var rows = SelfStatusPresenter.UnreadContainers(suspended, excluded, Name, DateTime.UtcNow);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsFault);                       // faults first: a symptom outranks a decision
        Assert.Equal("web", rows[0].Container);
        Assert.Contains("4 log fetches in a row timed out", rows[0].Detail);

        Assert.False(rows[1].IsFault);
        Assert.Equal("ghostunnel", rows[1].Container);
        Assert.Contains("reaches Docker", rows[1].Detail);
    }

    [Fact]
    public void With_nothing_wrong_the_unread_list_is_empty()
    {
        // A view that always shows something is one people stop reading.
        Assert.Empty(SelfStatusPresenter.UnreadContainers(
            Array.Empty<Whiskers.Services.LogMonitor.SuspendedContainer>(),
            Array.Empty<Whiskers.Services.LogMonitor.Hygiene.LogScanExclusion>(),
            Name, DateTime.UtcNow));
    }

    [Fact]
    public void A_paused_server_is_flagged_with_its_reason()
    {
        var servers = new FakeServerConfig(HostA);
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());
        var suspension = new LoopSuspensionService(
            new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance, new NoOutcomes());
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "investigating high load");

        var rows = SelfStatusPresenter.ServerRows(
            servers.GetEnabledServers(),
            new ServerBudget(settings, NullLogger<ServerBudget>.Instance),
            new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
                NullLogger<ServerCircuitBreaker>.Instance),
            suspension);

        var row = Assert.Single(rows);
        Assert.True(row.Paused);
        Assert.Equal("investigating high load", row.PauseReason);
        Assert.Equal(ServerCircuitState.Closed, row.Circuit);
    }
}
