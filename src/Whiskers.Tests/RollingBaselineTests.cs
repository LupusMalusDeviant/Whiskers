using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Tests.TestData;

namespace Whiskers.Tests;

/// <summary>
/// Deviation from a host's own normal (Plan-0004 WP3).
///
/// <para>The acceptance criterion is unusual and it is the whole point: the baseline must report the step on
/// 20 August <b>and</b> report, days later, that its own learned mean has drifted past the fixed threshold.
/// A baseline that only does the first thing goes quiet exactly when the problem has lasted longest.</para>
///
/// <para>Whiskers has been bitten by that shape twice already — the log-scan watermark that grew with every
/// failure, and an API-latency baseline that absorbed the slowdown it was meant to detect. This is the third
/// time the same trap has appeared in the same codebase, which is why WP3.4 is tested harder than WP3.1.</para>
/// </summary>
public class RollingBaselineTests
{
    private const double AbsoluteCpuThreshold = 90;

    /// <summary>Runs the incident bench through the baseline, one reading at a time.</summary>
    private static List<HostLoadFinding> Replay(BaselineSettings? settings = null)
    {
        var baseline = new RollingBaseline(settings);
        return CpuIncidentSeries.Build()
            .SelectMany(s => baseline.Observe(
                s.AtUtc, s.ServerId, s.ServerName, "host_cpu", s.HostCpuPercent, AbsoluteCpuThreshold))
            .ToList();
    }

    [Fact]
    public void The_step_is_reported_as_a_deviation_from_this_hosts_normal()
    {
        // First half of the acceptance criterion. 12% jumping to 98% is far outside anything this host has
        // done in the two days the baseline watched it.
        var anomaly = Replay()
            .Where(f => f.Kind == "host_cpu_anomaly" && f.What == FindingKind.Raised)
            .OrderBy(f => f.AtUtc)
            .FirstOrDefault();

        Assert.NotNull(anomaly);
        Assert.True(anomaly!.AtUtc >= CpuIncidentSeries.IncidentStart,
            $"reported at {anomaly.AtUtc:O}, before the incident even began");
        Assert.True(anomaly.AtUtc - CpuIncidentSeries.IncidentStart < TimeSpan.FromHours(1),
            $"took {(anomaly.AtUtc - CpuIncidentSeries.IncidentStart).TotalMinutes:F0} minutes to notice a 12→98 jump");
        Assert.Contains("standard deviations from this server's own normal", anomaly.Summary);
    }

    [Fact]
    public void The_baseline_says_when_it_has_started_treating_the_fault_as_normal()
    {
        // THE test for this package (WP3.4). After days of plateau the learned mean climbs past 90 and the
        // deviation rule goes quiet — that silence is the dangerous part, so the drift itself is reported.
        var drift = Replay()
            .Where(f => f.Kind == "host_cpu_baseline_drifted" && f.What == FindingKind.Raised)
            .OrderBy(f => f.AtUtc)
            .FirstOrDefault();

        Assert.NotNull(drift);
        Assert.Contains("has started treating this as ordinary", drift!.Summary);
        Assert.Contains("long enough to look normal", drift.Summary);

        // The plan asks for it around 23 August — three days into a six-day incident.
        Assert.True(drift.AtUtc > CpuIncidentSeries.IncidentStart,
            "the drift cannot precede the load that caused it");
        Assert.True(drift.AtUtc < CpuIncidentSeries.IncidentEnd,
            $"drift reported at {drift.AtUtc:O}, after the incident was already over — too late to be useful");
    }

    [Fact]
    public void The_drift_warning_arrives_while_the_incident_is_still_running()
    {
        // Sharper than the bound above: a warning that lands on day five of a six-day incident is a
        // post-mortem, not an alert.
        var drift = Replay().First(f => f.Kind == "host_cpu_baseline_drifted" && f.What == FindingKind.Raised);
        var into = drift.AtUtc - CpuIncidentSeries.IncidentStart;

        // Measured: 19.7 hours. The plan estimated 23 August — three days in — so this is better than asked
        // for, and the bound is set just above the measured value rather than at the plan's estimate. A
        // generous bound would let a regression that halves the reaction speed slip through unnoticed.
        Assert.True(into < TimeSpan.FromHours(24),
            $"the drift warning took {into.TotalHours:F1} hours; it was 19.7 when this was written");
    }

    [Fact]
    public void Nothing_is_judged_during_the_learning_period()
    {
        // WP3.3. A rule that cries wolf during its first two days never earns the benefit of the doubt
        // afterwards. The learning window here covers the whole run, so the deviation rule must stay silent —
        // but the drift guard is deliberately exempt, and this test must not accidentally forbid it.
        var baseline = new RollingBaseline(new BaselineSettings { LearningPeriod = TimeSpan.FromDays(30) });

        var findings = CpuIncidentSeries.Build()
            .SelectMany(s => baseline.Observe(
                s.AtUtc, s.ServerId, s.ServerName, "host_cpu", s.HostCpuPercent, AbsoluteCpuThreshold))
            .ToList();

        Assert.Empty(findings.Where(f => f.Kind == "host_cpu_anomaly"));
    }

    [Fact]
    public void The_drift_guard_speaks_even_while_the_baseline_is_still_learning()
    {
        // Deliberately exempt from the learning period: if the first 48 hours are already spent above the
        // threshold, that is the most important thing this rule could say, and "still learning" would be the
        // worst possible moment to stay silent.
        var baseline = new RollingBaseline(new BaselineSettings { LearningPeriod = TimeSpan.FromDays(30) });

        var findings = CpuIncidentSeries.Build()
            .SelectMany(s => baseline.Observe(
                s.AtUtc, s.ServerId, s.ServerName, "host_cpu", s.HostCpuPercent, AbsoluteCpuThreshold))
            .ToList();

        Assert.Contains(findings, f => f.Kind == "host_cpu_baseline_drifted");
    }

    [Fact]
    public void The_deviation_rule_goes_quiet_once_the_fault_becomes_the_new_normal()
    {
        // Not a defect — the documented reason WP3.4 exists. The anomaly is raised eleven minutes after the
        // step and closes again within the hour, because the mean has moved to meet the value. Anyone reading
        // that all-clear without the drift warning would conclude the server had recovered.
        var cpu = Replay().Where(f => f.Kind == "host_cpu_anomaly").OrderBy(f => f.AtUtc).ToList();

        var raised = cpu.First(f => f.What == FindingKind.Raised);
        var cleared = cpu.First(f => f.What == FindingKind.Cleared && f.AtUtc > raised.AtUtc);

        Assert.True(cleared.AtUtc - raised.AtUtc < TimeSpan.FromHours(2));
        Assert.True(cleared.AtUtc < CpuIncidentSeries.IncidentEnd,
            "the anomaly went quiet while the incident was still running — which is exactly why the drift guard exists");
    }

    [Fact]
    public void A_steady_host_produces_nothing_at_all()
    {
        // The counterweight. A server doing the same thing all week must be silent, or the deviation rule is
        // just an expensive way to generate noise.
        var baseline = new RollingBaseline();
        var start = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
        var findings = new List<HostLoadFinding>();

        for (var i = 0; i < 6000; i++)
        {
            // A gentle daily rhythm between 20% and 30% — a normal server, not a flat line.
            var cpu = 25 + 5 * Math.Sin(i / 720.0 * Math.PI);
            findings.AddRange(baseline.Observe(
                start.AddMinutes(i), "quiet", "Quiet", "host_cpu", cpu, AbsoluteCpuThreshold));
        }

        Assert.Empty(findings);
    }

    [Fact]
    public void A_perfectly_flat_host_does_not_become_the_noisiest_one()
    {
        // Without a floor under the standard deviation, a host that never varies has a deviation approaching
        // zero, and the first wobble is an infinite z-score. The quietest servers would alert the most.
        var baseline = new RollingBaseline();
        var start = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
        var findings = new List<HostLoadFinding>();

        for (var i = 0; i < 5000; i++)
        {
            var cpu = i == 4500 ? 12.5 : 12.0;   // five thousand identical readings, then a rounding wobble
            findings.AddRange(baseline.Observe(
                start.AddMinutes(i), "flat", "Flat", "host_cpu", cpu, AbsoluteCpuThreshold));
        }

        Assert.Empty(findings);
    }

    [Fact]
    public void The_learned_state_is_readable_including_whether_it_is_still_learning()
    {
        // WP3.3 asks for a visible "still learning" rather than silent guessing — so the state has to be
        // something a view can render, not just an internal flag.
        var baseline = new RollingBaseline();
        var start = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 100; i++)
            baseline.Observe(start.AddMinutes(i), "srv", "Srv", "host_cpu", 20, AbsoluteCpuThreshold);

        var state = Assert.Single(baseline.States(start.AddMinutes(100)));
        Assert.True(state.StillLearning);
        Assert.Equal(100, state.Samples);
        Assert.Equal(20, state.Mean, precision: 0);

        Assert.False(baseline.States(start.AddDays(3)).Single().StillLearning);
    }
}
