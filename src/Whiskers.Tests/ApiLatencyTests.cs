using Whiskers.Services.Metrics.HostLoad;

namespace Whiskers.Tests;

/// <summary>
/// The Docker API response time as a signal (Plan-0004 WP4).
///
/// <para>An overloaded daemon has a fingerprint that names no culprit: calls that took 100 ms start taking
/// seconds. On 2026-08-26 every Whiskers call to that host went through exactly that treacle for six days,
/// and nothing measured it — the only visible symptom was that things "felt slow".</para>
/// </summary>
public class ApiLatencyTests
{
    private static readonly DateTime Noon = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Feeds a growing window through the rule one reading at a time, the way the real loop does.</summary>
    private static List<HostLoadFinding> Replay(IReadOnlyList<TimeSpan> series, ApiLatencySettings? settings = null)
    {
        var evaluator = new ApiLatencyEvaluator(settings: settings);
        var window = new List<TimeSpan>();
        var findings = new List<HostLoadFinding>();

        for (var i = 0; i < series.Count; i++)
        {
            window.Add(series[i]);
            var finding = evaluator.Evaluate(Noon.AddMinutes(i), "host-a", "Host-A", window);
            if (finding is not null) findings.Add(finding);
        }

        return findings;
    }

    private static List<TimeSpan> Steady(int count, double ms) =>
        Enumerable.Range(0, count).Select(_ => TimeSpan.FromMilliseconds(ms)).ToList();

    [Fact]
    public void A_daemon_that_slows_to_a_crawl_is_reported()
    {
        // The assertion that matters: this has to FIRE. 100 ms becoming 5 s is the incident's own fingerprint.
        var series = Steady(40, 100);
        series.AddRange(Steady(40, 5000));

        var finding = Replay(series).FirstOrDefault(f => f.What == FindingKind.Raised);

        Assert.NotNull(finding);
        Assert.Equal("host_api_slow", finding!.Kind);
        Assert.Contains("slower than usual", finding.Summary);
        Assert.Contains("5000 ms", finding.Summary);
    }

    [Fact]
    public void The_alert_says_it_does_not_yet_know_the_cause()
    {
        // This signal is deliberately blind to the culprit — it is the daemon being slow, which is a
        // different claim from "a container is doing it". Overstating it would send someone to the wrong
        // place, and the 2026-08-26 cause was not in any container at all.
        var series = Steady(40, 100);
        series.AddRange(Steady(40, 5000));

        var finding = Replay(series).First(f => f.What == FindingKind.Raised);

        Assert.Contains("says nothing yet about what is loading it", finding.Summary);
    }

    [Fact]
    public void A_healthy_host_with_ordinary_variation_stays_quiet()
    {
        // Docker call times bounce around by more than a factor of two on a perfectly healthy host. A rule
        // that fires on that is one people switch off within a day.
        var jitter = new[] { 90.0, 140, 110, 200, 95, 160, 105, 130, 180, 100 };
        var series = Enumerable.Range(0, 80).Select(i => TimeSpan.FromMilliseconds(jitter[i % jitter.Length])).ToList();

        Assert.Empty(Replay(series));
    }

    [Fact]
    public void An_idle_host_answering_in_microseconds_is_not_judged_by_ratio()
    {
        // 2 ms becoming 8 ms is a factor of four and means nothing. Without the floor this rule would report
        // every host that briefly stopped hitting a cache.
        var series = Steady(40, 2);
        series.AddRange(Steady(40, 8));

        Assert.Empty(Replay(series));
    }

    [Fact]
    public void Nothing_is_claimed_before_there_is_enough_history()
    {
        // A baseline built from a handful of readings would call the next one an anomaly. A rule that cries
        // wolf while it is still learning gets switched off before it is ever useful.
        var series = Steady(5, 100);
        series.AddRange(Steady(5, 9000));

        Assert.Empty(Replay(series));
    }

    [Fact]
    public void A_single_slow_call_is_not_a_verdict()
    {
        // One 8-second outlier in a window of forty. A mean would have been dragged far enough to alert; the
        // median is why this stays quiet.
        var series = Steady(40, 100);
        series[20] = TimeSpan.FromSeconds(8);

        Assert.Empty(Replay(series));
    }

    [Fact]
    public void A_daemon_that_recovers_gets_an_all_clear()
    {
        // Same rule as the host alerts: the operator who was told about the slowdown has to be told it ended,
        // or the next alert reads as a continuation of the old one.
        var series = Steady(40, 100);
        series.AddRange(Steady(60, 5000));
        series.AddRange(Steady(60, 100));

        var findings = Replay(series);

        Assert.Contains(findings, f => f.What == FindingKind.Raised);
        Assert.Contains(findings, f => f.What == FindingKind.Cleared);
        Assert.Equal("host_api_slow_recovered", findings.Last(f => f.What == FindingKind.Cleared).EventType);
    }

    [Fact]
    public void The_ratio_is_measured_against_the_hosts_own_history_not_a_fixed_number()
    {
        // A Raspberry Pi over a tunnel and a local socket differ by an order of magnitude while both are
        // perfectly healthy. A fixed millisecond threshold would be wrong for one of them whatever it was set
        // to — so a slow-but-consistent host must stay quiet.
        var slowButSteady = Steady(80, 2000);

        Assert.Empty(Replay(slowButSteady));
    }
}
