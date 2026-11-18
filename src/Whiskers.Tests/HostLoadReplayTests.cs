using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Tests.TestData;

namespace Whiskers.Tests;

/// <summary>
/// The replay harness and the rules it proves (Plan-0004 WP0/WP1/WP2).
///
/// <para>Whiskers recorded roughly 8,900 measurements of Host-B over six days, practically every one above
/// 98%, and evaluated none of them — the load fell through the gap between "per container" and "disk",
/// because <c>dockerd</c> runs in no container. These tests push a reconstruction of that week through the
/// new rules and demand that they <b>fire</b>, at the right time, with the right cause named.</para>
///
/// <para>What this cannot show: whether the rules stay quiet through a normal week. The series has no daily
/// cycle, no backup peak and no noise, because inventing those would produce evidence about false alarms that
/// the data cannot actually support. See <see cref="CpuIncidentSeries"/>.</para>
/// </summary>
public class HostLoadReplayTests
{
    /// <summary>The replay harness (WP0.2): a whole week through the rules in a fraction of a second, with
    /// findings dated by SAMPLE time. Nothing here consults the wall clock.</summary>
    private static List<HostLoadFinding> Replay(
        IReadOnlyList<HostSample> series, HostLoadThresholds? thresholds = null)
    {
        var evaluator = new HostLoadEvaluator(thresholds);
        return series.SelectMany(evaluator.Evaluate).ToList();
    }

    // --- WP0: the harness and the dataset ----------------------------------------------------------------

    [Fact]
    public void The_series_contains_the_step_and_the_recovery_the_report_documents()
    {
        // The plan's acceptance criterion for WP0, checked against the series itself rather than against a
        // rule — if the test bench does not contain the incident, nothing measured on it means anything.
        var series = CpuIncidentSeries.Build();

        var beforeStep = series.Last(s => s.AtUtc < CpuIncidentSeries.IncidentStart);
        var plateau = series.First(s => s.AtUtc >= CpuIncidentSeries.IncidentStart.AddMinutes(5));
        var afterRecovery = series.First(s => s.AtUtc >= CpuIncidentSeries.IncidentEnd);

        Assert.Equal(12.0, beforeStep.HostCpuPercent, precision: 1);
        Assert.Equal(98.3, plateau.HostCpuPercent, precision: 1);
        Assert.Equal(9.0, afterRecovery.HostCpuPercent, precision: 1);

        // Six days of it, not a spike.
        var elevated = series.Count(s => s.HostCpuPercent > 90);
        Assert.True(elevated > 8000, $"only {elevated} samples above 90% — the report describes about 8,900");
    }

    [Fact]
    public void The_replay_dates_its_findings_by_sample_time_and_not_by_the_clock()
    {
        // The property that makes a replay worth anything. A rule reading DateTime.UtcNow would report today
        // and prove nothing about August.
        var findings = Replay(CpuIncidentSeries.Build());

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.True(
            f.AtUtc.Year == 2026 && f.AtUtc.Month == 8,
            $"finding dated {f.AtUtc:O} — the replay is reading the wall clock somewhere"));
    }

    // --- WP1: the host threshold -------------------------------------------------------------------------

    [Fact]
    public void The_incident_is_reported_within_twenty_minutes_of_the_step()
    {
        // The acceptance criterion of WP1, and the whole reason this package exists: six days versus twenty
        // minutes. The report itself says a host threshold "hätte am 20.08. um 14:17 gemeldet".
        var findings = Replay(CpuIncidentSeries.Build());

        var first = findings.Where(f => f.Kind == "host_cpu_high").OrderBy(f => f.AtUtc).FirstOrDefault();

        Assert.NotNull(first);
        var delay = first!.AtUtc - CpuIncidentSeries.IncidentStart;
        Assert.True(delay <= TimeSpan.FromMinutes(20),
            $"the host-CPU rule took {delay.TotalMinutes:F0} minutes; the incident ran for six days");
        Assert.Contains("whole machine", first.Summary);

        // Pinned to the exact minute, not just "within twenty". The report says a host threshold would have
        // fired at 14:17; this fires at 14:14, and a change that quietly pushed it to 14:19 would still pass
        // the window check above while meaning the rule had got slower.
        Assert.Equal(new DateTime(2026, 8, 20, 14, 14, 0, DateTimeKind.Utc), first.AtUtc);
    }

    [Fact]
    public void A_short_peak_is_not_reported()
    {
        // The counterweight. A rule that fires on every build peak gets muted, and a muted rule would have
        // missed this incident just as thoroughly as no rule at all.
        var start = new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);
        var series = new List<HostSample>();

        for (var i = 0; i < 60; i++)
        {
            // Five minutes at 99%, then back to idle.
            var cpu = i is >= 10 and < 15 ? 99.0 : 11.0;
            series.Add(new HostSample(
                start.AddMinutes(i), "host-b", "Host-B", cpu, 24.0, 1e9, 4e9, CoreCount: 2));
        }

        Assert.Empty(Replay(series).Where(f => f.Kind == "host_cpu_high"));
    }

    [Fact]
    public void A_lasting_breach_is_reported_once_rather_than_every_minute()
    {
        // Six days at one alert a minute is 8,900 alerts, which is the same as none.
        var findings = Replay(CpuIncidentSeries.Build()).Where(f => f.Kind == "host_cpu_high").ToList();

        Assert.True(findings.Count is > 0 and < 30,
            $"{findings.Count} host-CPU alerts over six days — escalation should repeat rarely, not hourly");
    }

    [Fact]
    public void Memory_stays_quiet_during_a_CPU_incident()
    {
        // If this ever fires, the rule is reading the wrong field — and an alert about the wrong resource
        // sends someone looking in the wrong place while the real cause keeps running.
        Assert.Empty(Replay(CpuIncidentSeries.Build()).Where(f => f.Kind == "host_memory_high"));
    }

    // --- WP5: one open finding, escalation, all-clear -----------------------------------------------------

    /// <summary>A series that goes high, stays high for `highMinutes`, then drops to `after` and stays there.</summary>
    private static List<HostSample> Spike(int highMinutes, double high = 98.0, double after = 9.0, int tailMinutes = 60)
    {
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = new List<HostSample>();
        for (var i = 0; i < highMinutes + tailMinutes; i++)
            series.Add(new HostSample(
                start.AddMinutes(i), "host-a", "Host-A",
                i < highMinutes ? high : after, 24.0, 1e9, 4e9, CoreCount: 2));
        return series;
    }

    [Fact]
    public void A_finished_incident_is_closed_with_an_all_clear()
    {
        // The gap in the first cut of this package: a server that went from 98% back to 9% produced silence.
        // The operator was told about the fire and never told it was out — after which the next alert is read
        // as "probably still the old one".
        var findings = Replay(Spike(highMinutes: 30));

        var cpu = findings.Where(f => f.Kind == "host_cpu_high").ToList();
        Assert.Equal(FindingKind.Raised, cpu[0].What);
        Assert.Equal(FindingKind.Cleared, cpu[^1].What);
        Assert.Contains("back to 9% CPU", cpu[^1].Summary);
        Assert.Contains("over the threshold for 30 minutes", cpu[^1].Summary);
    }

    [Fact]
    public void The_plan_acceptance_case_produces_exactly_one_alert_and_one_all_clear()
    {
        // Plan-0004 WP5 verbatim: a five-minute peak produces nothing; a thirty-minute state produces exactly
        // one alert, closed at the end.
        Assert.Empty(Replay(Spike(highMinutes: 5)).Where(f => f.Kind == "host_cpu_high"));

        var sustained = Replay(Spike(highMinutes: 30)).Where(f => f.Kind == "host_cpu_high").ToList();

        Assert.Equal(2, sustained.Count);
        Assert.Equal(FindingKind.Raised, sustained[0].What);
        Assert.Equal(FindingKind.Cleared, sustained[1].What);
    }

    [Fact]
    public void A_server_that_only_just_dips_below_the_threshold_is_not_declared_clear()
    {
        // The hysteresis, tested where it actually bites. A host that falls from 98% to 87% and stays there
        // is barely better than it was: it is under the line but nowhere near recovered. Declaring an
        // all-clear would tell the operator the problem is over while the machine is still nearly saturated,
        // and the next time it crosses back the alert reads like a new incident rather than a continuation.
        //
        // The earlier version of this test alternated across the threshold every minute and proved nothing —
        // the alternation was faster than the confirmation window, so no all-clear could form with OR without
        // the margin. It passed against a build with the hysteresis removed.
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = new List<HostSample>();
        for (var i = 0; i < 120; i++)
        {
            var cpu = i < 30 ? 98.0 : 87.0;   // 87 is below the 90 threshold but above the 85 clear line
            series.Add(new HostSample(start.AddMinutes(i), "wobbly", "Wobbly", cpu, 24.0, 1e9, 4e9, CoreCount: 2));
        }

        var findings = Replay(series).Where(f => f.Kind == "host_cpu_high").ToList();

        Assert.Equal(FindingKind.Raised, Assert.Single(findings).What);
    }

    [Fact]
    public void A_real_recovery_below_the_clear_line_does_produce_the_all_clear()
    {
        // The counterweight: the margin must not swallow genuine recoveries, or the finding never closes and
        // WP5.4's stale-findings count climbs forever.
        var findings = Replay(Spike(highMinutes: 30, after: 84.0)).Where(f => f.Kind == "host_cpu_high").ToList();

        Assert.Equal(
            new[] { FindingKind.Raised, FindingKind.Cleared },
            findings.Select(f => f.What));
    }

    [Fact]
    public void A_breach_that_ends_before_it_was_ever_announced_stays_silent()
    {
        // Otherwise the first thing an operator hears about a problem is that it is over — which is noise
        // dressed as information.
        Assert.Empty(Replay(Spike(highMinutes: 5)));
    }

    [Fact]
    public void A_lasting_breach_says_it_again_only_when_it_gets_worse()
    {
        // WP5.1: escalate, do not repeat. Six days of "still 98%" is 8,900 messages that all say the same
        // thing, and a channel like that is filtered within a day.
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = new List<HostSample>();
        for (var i = 0; i < 200; i++)
        {
            // 91% for a long while, then climbing past the escalation step.
            var cpu = i < 120 ? 91.0 : 99.0;
            series.Add(new HostSample(start.AddMinutes(i), "creep", "Creep", cpu, 24.0, 1e9, 4e9, CoreCount: 2));
        }

        var findings = Replay(series).Where(f => f.Kind == "host_cpu_high").ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingKind.Raised, findings[0].What);
        Assert.Equal(FindingKind.Escalated, findings[1].What);
        Assert.Contains("Getting worse", findings[1].Summary);
    }

    [Fact]
    public void An_alert_carries_how_long_it_has_been_going()
    {
        // WP5.3. "98%" is a different problem at two minutes and at six days, and an alert that cannot be
        // triaged is one that gets triaged last.
        var raised = Replay(CpuIncidentSeries.Build())
            .First(f => f.Kind == "host_cpu_high" && f.What == FindingKind.Raised);

        Assert.Equal(TimeSpan.FromMinutes(10), raised.OpenFor);
        Assert.Contains("for 10 minutes", raised.Summary);
        Assert.Equal(90, raised.Threshold);
    }

    [Fact]
    public void An_open_finding_that_never_closes_is_visible_as_such()
    {
        // WP5.4: if this list only ever grows, the closing path is broken — and a monitor whose alerts never
        // close stops being read long before anyone works out why.
        var evaluator = new HostLoadEvaluator();
        foreach (var sample in CpuIncidentSeries.Build(
                     CpuIncidentSeries.IncidentStart, CpuIncidentSeries.IncidentStart.AddDays(3)))
            evaluator.Evaluate(sample);

        var open = evaluator.OpenFindings();

        Assert.Contains(open, f => f.Kind == "host_cpu_high");
        var oldest = open.First();
        Assert.True(oldest.SinceUtc < CpuIncidentSeries.IncidentStart.AddMinutes(15));
    }

    [Fact]
    public void An_all_clear_never_arrives_under_the_alarms_own_name()
    {
        // Every channel, filter rule and severity mapping keys off the event type. A closing message labelled
        // host_cpu_high would be rendered, coloured and escalated as a fresh alarm — the operator would read
        // "server back to 9%" in red, next to a warning icon, and trust the next one less.
        var findings = Replay(Spike(highMinutes: 30)).Where(f => f.Kind == "host_cpu_high").ToList();

        var raised = findings.First(f => f.What == FindingKind.Raised);
        var cleared = findings.First(f => f.What == FindingKind.Cleared);

        Assert.Equal("host_cpu_high", raised.EventType);
        Assert.Equal("Warning", raised.Severity);

        Assert.Equal("host_cpu_high_recovered", cleared.EventType);
        Assert.Equal("Info", cleared.Severity);
    }

    [Fact]
    public void A_closed_finding_leaves_the_open_list()
    {
        var evaluator = new HostLoadEvaluator();
        foreach (var sample in Spike(highMinutes: 30))
            evaluator.Evaluate(sample);

        Assert.Empty(evaluator.OpenFindings());
    }

    // --- WP2: load no container explains -----------------------------------------------------------------

    [Fact]
    public void The_unexplained_load_rule_names_the_class_of_cause()
    {
        // The most specific signal of the whole incident: the containers accounted for 12% of the machine
        // while the machine sat at 98%. Establishing that by hand took six days.
        var findings = Replay(CpuIncidentSeries.Build())
            .Where(f => f.Kind == "host_cpu_unexplained").OrderBy(f => f.AtUtc).ToList();

        var first = Assert.IsType<HostLoadFinding>(findings.FirstOrDefault());
        Assert.True(first.AtUtc - CpuIncidentSeries.IncidentStart <= TimeSpan.FromMinutes(20));
        Assert.Contains("host process is the likely cause", first.Summary);
        Assert.Contains("hint rather than proof", first.Summary);
    }

    [Fact]
    public void The_two_CPU_conventions_are_reconciled_before_they_are_compared()
    {
        // WP2.1, and the main way to get this package wrong. The host figure is percent of the MACHINE; the
        // container figure is Docker's, where one busy core is 100 and a 2-core box reaches 200. Subtracting
        // them raw gives a negative "unexplained" load on a busy machine — the alert would be silent exactly
        // when it is needed.
        var sample = new HostSample(
            DateTime.UtcNow, "host-b", "Host-B",
            HostCpuPercent: 98.0,
            ContainerCpuPercentSum: 120.0,   // Docker scale: 1.2 cores busy
            MemoryUsedBytes: 1e9, MemoryTotalBytes: 4e9, CoreCount: 2);

        Assert.Equal(60.0, sample.ContainerCpuPercentOfMachine, precision: 1);
        Assert.Equal(38.0, sample.UnexplainedCpuPercent, precision: 1);
    }

    [Fact]
    public void On_a_four_core_host_the_unexplained_load_is_still_found()
    {
        // The behavioural counterpart to the arithmetic test above, and the more valuable one. Without the
        // scale conversion the container sum is over-counted, the gap comes out too SMALL, and the alert
        // stays silent — a false negative, which is the failure mode this entire package exists to remove.
        //
        // Here: a 4-core host at 95% of the machine, containers accounting for 30% of it (120 in Docker's
        // scale). Converted, 65 points are unexplained and the rule fires. Compared raw, 95 − 120 is negative,
        // the gap clamps to zero, and six days pass with nobody told. Host-B's own numbers do not catch
        // this — 98 − 24 is still over the threshold either way — so it takes a bigger box to expose it.
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = Enumerable.Range(0, 60).Select(i => new HostSample(
            start.AddMinutes(i), "bigbox", "BigBox",
            HostCpuPercent: 95.0,
            ContainerCpuPercentSum: 120.0,
            MemoryUsedBytes: 1e9, MemoryTotalBytes: 8e9, CoreCount: 4)).ToList();

        var finding = Assert.Single(Replay(series).Where(f => f.Kind == "host_cpu_unexplained"));

        Assert.Equal(65.0, finding.Value, precision: 1);
    }

    [Fact]
    public void A_busy_machine_whose_containers_explain_the_load_raises_no_unexplained_alert()
    {
        // The other half: a genuinely busy fleet is not a fault. Without this the rule would fire on every
        // server that is simply doing its job, and be switched off within a week.
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = Enumerable.Range(0, 240).Select(i => new HostSample(
            start.AddMinutes(i), "busy", "BusyBox",
            HostCpuPercent: 95.0,
            ContainerCpuPercentSum: 190.0,   // 95% of a 2-core machine — the containers ARE the load
            MemoryUsedBytes: 1e9, MemoryTotalBytes: 4e9, CoreCount: 2)).ToList();

        var findings = Replay(series);

        Assert.Empty(findings.Where(f => f.Kind == "host_cpu_unexplained"));
        Assert.NotEmpty(findings.Where(f => f.Kind == "host_cpu_high"));   // still busy, still worth saying
    }

    [Fact]
    public void A_server_that_recovers_and_breaks_again_is_reported_again()
    {
        // The repeat window must not swallow a NEW breach. A server that flaps in and out is a server with a
        // problem, and going quiet after the first report is how it becomes somebody's Friday evening.
        var start = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var series = new List<HostSample>();

        for (var i = 0; i < 120; i++)
        {
            // 0-39 high, 40-59 recovered, 60-119 high again — all well inside the 6-hour repeat window.
            var cpu = i < 40 || i >= 60 ? 98.0 : 10.0;
            series.Add(new HostSample(
                start.AddMinutes(i), "flappy", "Flappy", cpu, 24.0, 1e9, 4e9, CoreCount: 2));
        }

        var findings = Replay(series).Where(f => f.Kind == "host_cpu_high").ToList();

        // Raised, closed when it recovered, raised again — the recovery is now announced too (WP5.2), so the
        // sequence is three statements rather than two silences and two alerts.
        Assert.Equal(
            new[] { FindingKind.Raised, FindingKind.Cleared, FindingKind.Raised },
            findings.Select(f => f.What));
    }
}
