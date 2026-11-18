using Whiskers.Models.Cve;
using Whiskers.Services.Cve;

namespace Whiskers.Tests;

/// <summary>
/// The numbers behind the <c>whiskers_cve_*</c> series (2026-08-27).
///
/// <para>A failed CVE scan deliberately keeps the previous results rather than reporting a false all-clear.
/// That is the right trade, and it has a consequence nobody had measured: a target whose scanner broke months
/// ago looks exactly like one scanned this morning — the same findings, still plausible, simply never
/// changing. The identity-provider image sat like that from July to late August. The only thing that told the two
/// apart was the age of the data, and nothing was watching it.</para>
///
/// <para>So these tests care mostly about staleness: that it is noticed, that it is not invented, and that one
/// frozen target among many fresh ones does not get averaged into silence.</para>
/// </summary>
public class CveMetricsTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private static CveScanResult Target(string serverId, string container, DateTime scannedAt,
        params CveSeverity[] severities)
        => new()
        {
            ServerId = serverId,
            Source = CveSource.Container,
            ContainerId = container,
            ContainerName = container,
            ScannedAt = scannedAt,
            Findings = severities.Select((s, i) => new CveFinding
            {
                ServerId = serverId, Source = CveSource.Container, ContainerId = container,
                ContainerName = container, CveId = $"CVE-2026-{1000 + i}", Package = "openssl", Severity = s
            }).ToList()
        };

    private static CveServerMetrics Only(CveFleetMetrics m) => Assert.Single(m.PerServer);

    // ---- staleness: the reason this exists ------------------------------------------------------------

    [Fact]
    public void A_target_frozen_since_July_is_counted_as_stale()
    {
        // The identity-provider case, to the week. Its findings were present and unchanged the whole time; the data
        // age is the only signal that separates that from a healthy target.
        var result = CveMetrics.Build(
            [Target("local", "idp-worker", Now.AddDays(-50), CveSeverity.High)], Interval, Now);

        var server = Only(result);
        Assert.Equal(1, server.StaleTargets);
        Assert.Equal(50, server.OldestDataAge.TotalDays, precision: 0);
    }

    [Fact]
    public void A_target_scanned_this_cycle_is_not_stale()
    {
        // The other direction, and the one that decides whether the alert is worth having. A staleness signal
        // that fires on healthy targets gets silenced within a week and then it is not a signal at all.
        var result = CveMetrics.Build(
            [Target("local", "web", Now.AddHours(-1), CveSeverity.Low)], Interval, Now);

        Assert.Equal(0, Only(result).StaleTargets);
    }

    [Fact]
    public void A_single_missed_cycle_is_not_yet_stale()
    {
        // A failed scan retries in ~15 minutes, so one missed refresh repairs itself. Calling that "stale"
        // would put a number above zero on an ordinary afternoon. Two consecutive misses is a scanner that is
        // not coming back.
        var oneMissed = CveMetrics.Build(
            [Target("local", "web", Now.AddHours(-13), CveSeverity.Low)], Interval, Now);
        var twoMissed = CveMetrics.Build(
            [Target("local", "web", Now.AddHours(-25), CveSeverity.Low)], Interval, Now);

        Assert.Equal(0, Only(oneMissed).StaleTargets);
        Assert.Equal(1, Only(twoMissed).StaleTargets);
    }

    [Fact]
    public void One_frozen_target_among_many_fresh_ones_is_not_averaged_away()
    {
        // Why the age is a maximum and not a mean. Nine healthy targets and one abandoned one is exactly the
        // shape of the incident, and an average would report it as "about five hours old" — reassuring, and
        // wrong about the only target that matters.
        var targets = Enumerable.Range(0, 9)
            .Select(i => Target("local", $"c{i}", Now.AddHours(-1), CveSeverity.Low))
            .Append(Target("local", "idp", Now.AddDays(-50), CveSeverity.Critical))
            .ToList();

        var server = Only(CveMetrics.Build(targets, Interval, Now));

        Assert.Equal(1, server.StaleTargets);
        Assert.True(server.OldestDataAge > TimeSpan.FromDays(49));
    }

    [Fact]
    public void The_threshold_follows_the_configured_interval()
    {
        // An hourly scan and a twice-daily one cannot share a fixed cutoff: the same 20-hour-old data is a
        // dead scanner in the first case and perfectly normal in the second.
        var target = new[] { Target("local", "web", Now.AddHours(-20), CveSeverity.Low) };

        Assert.Equal(1, Only(CveMetrics.Build(target, TimeSpan.FromHours(1), Now)).StaleTargets);
        Assert.Equal(0, Only(CveMetrics.Build(target, TimeSpan.FromHours(12), Now)).StaleTargets);
    }

    [Fact]
    public void A_scan_timestamp_from_the_future_reports_no_age_rather_than_a_negative_one()
    {
        // A clock that jumped forward and back would otherwise render a negative gauge, which reads as
        // "scanned very recently" — the opposite of what is known.
        var server = Only(CveMetrics.Build(
            [Target("local", "web", Now.AddHours(3), CveSeverity.Low)], Interval, Now));

        Assert.True(server.OldestDataAge >= TimeSpan.Zero);
    }

    // ---- the counts ------------------------------------------------------------------------------------

    [Fact]
    public void Distinct_vulnerabilities_are_counted_apart_from_their_instances()
    {
        // On the real fleet this was 550 distinct CVEs behind 4658 findings. Reporting only the second number
        // describes the size of the display, not the size of the problem.
        var shared = new[]
        {
            Target("a", "web", Now, CveSeverity.High),
            Target("b", "web", Now, CveSeverity.High)   // same generated CVE ids
        };

        var result = CveMetrics.Build(shared, Interval, Now);

        Assert.Equal(2, result.PerServer.Count);
        Assert.Equal(1, result.DistinctCveIds);         // one vulnerability...
        Assert.Equal(2, result.PerServer.Sum(s => s.BySeverity.Sum(b => b.Count)));  // ...two instances
    }

    [Fact]
    public void Severities_with_nothing_in_them_are_absent_rather_than_zero()
    {
        // A permanent "critical 0" line on every server is one more row to stop reading, and the day it turns
        // into a 1 it gets skipped along with the rest.
        var server = Only(CveMetrics.Build(
            [Target("local", "web", Now, CveSeverity.Low, CveSeverity.Low)], Interval, Now));

        var entry = Assert.Single(server.BySeverity);
        Assert.Equal(CveSeverity.Low, entry.Severity);
        Assert.Equal(2, entry.Count);
    }

    [Fact]
    public void The_worst_severity_is_listed_first()
    {
        var server = Only(CveMetrics.Build(
            [Target("local", "web", Now, CveSeverity.Low, CveSeverity.Critical, CveSeverity.Medium)],
            Interval, Now));

        Assert.Equal(CveSeverity.Critical, server.BySeverity[0].Severity);
    }

    [Fact]
    public void Servers_are_kept_apart_and_matched_without_regard_to_case()
    {
        // servers.json is hand-edited; "Host-B" and "host-b" are one machine and must not become two
        // series that each tell half the story.
        var result = CveMetrics.Build(
        [
            Target("Host-B", "a", Now, CveSeverity.High),
            Target("host-b", "b", Now.AddDays(-50), CveSeverity.Low)
        ], Interval, Now);

        var server = Only(result);
        Assert.Equal(1, server.StaleTargets);
        Assert.Equal(2, server.BySeverity.Sum(b => b.Count));
    }

    [Fact]
    public void An_empty_fleet_produces_nothing_rather_than_a_row_of_zeroes()
    {
        var result = CveMetrics.Build([], Interval, Now);

        Assert.Empty(result.PerServer);
        Assert.Equal(0, result.DistinctCveIds);
    }

    // ---- naming the guilty target (the half /metrics cannot carry) ------------------------------------

    [Fact]
    public void The_stale_targets_are_named_worst_first()
    {
        // The count on the endpoint tells an operator that something has stopped; without this they have
        // nowhere to go with that. Worst first, because the one frozen longest is the one to look at.
        var stale = CveMetrics.StaleTargets(
        [
            Target("local", "fresh", Now.AddHours(-1), CveSeverity.Low),
            Target("local", "idp-worker", Now.AddDays(-50), CveSeverity.High),
            Target("local", "old-web", Now.AddDays(-3), CveSeverity.Low)
        ], Interval, Now);

        Assert.Equal(2, stale.Count);
        Assert.Equal("idp-worker", stale[0].Target);
        Assert.Equal("old-web", stale[1].Target);
        Assert.DoesNotContain(stale, t => t.Target == "fresh");
    }

    [Fact]
    public void A_stale_host_scan_is_named_as_the_host_not_as_an_unnamed_container()
    {
        // The OS target has no container name. Rendering it blank would put a nameless "!" line in front of
        // somebody who then has nothing to search for.
        var stale = CveMetrics.StaleTargets(
        [
            new CveScanResult { ServerId = "host-a", Source = CveSource.Os, ScannedAt = Now.AddDays(-40) }
        ], Interval, Now);

        Assert.Equal("host OS", Assert.Single(stale).Target);
    }

    [Fact]
    public void Nothing_stale_names_nobody()
    {
        var stale = CveMetrics.StaleTargets(
            [Target("local", "web", Now.AddHours(-2), CveSeverity.Low)], Interval, Now);

        Assert.Empty(stale);
    }

    // ---- targets whose scan failed: absent used to look identical to clean ------------------------------

    private static CveScanResult Failed(string serverId, string container, string error, DateTime scannedAt)
        => new()
        {
            ServerId = serverId, Source = CveSource.Container, ContainerId = container,
            ContainerName = container, ScannedAt = scannedAt, Error = error
        };

    [Fact]
    public void A_target_whose_scan_failed_is_counted_and_not_mistaken_for_clean()
    {
        // The 2026-08-28 case: two running containers on host-f could not be scanned because their local
        // image layers were damaged. Neither had ever been scanned successfully, so nothing was stored and
        // they were absent from every list — which reads exactly like "no findings".
        var server = Only(CveMetrics.Build(
        [
            Target("host-f", "caddy", Now, CveSeverity.Low),
            Failed("host-f", "ghostunnel", "not found in tar", Now)
        ], Interval, Now));

        Assert.Equal(1, server.FailedTargets);
        // ...and its zero findings must not quietly inflate the "everything is fine" picture
        Assert.Equal(1, server.BySeverity.Sum(b => b.Count));
    }

    [Fact]
    public void A_healthy_fleet_reports_no_failures()
    {
        // The other direction: a permanent non-zero here would be ignored within a week.
        var server = Only(CveMetrics.Build(
            [Target("local", "web", Now, CveSeverity.Low)], Interval, Now));

        Assert.Equal(0, server.FailedTargets);
    }

    [Fact]
    public void The_failed_targets_are_named_with_their_reason()
    {
        // A count with no target is a number nobody can act on — the operator needs to know WHICH container
        // and WHY, because the fix differs (damaged image layer, missing credentials, host unreachable).
        var failed = CveMetrics.FailedTargets(
        [
            Target("host-f", "caddy", Now, CveSeverity.Low),
            Failed("host-f", "ghostunnel", "not found in tar", Now),
            Failed("host-f", "sentinel", "not found in tar", Now)
        ]);

        Assert.Equal(2, failed.Count);
        Assert.Contains(failed, f => f.Target == "ghostunnel" && f.Error.Contains("not found in tar"));
        Assert.DoesNotContain(failed, f => f.Target == "caddy");
    }

    [Fact]
    public void A_failed_target_reports_what_is_still_known_about_it()
    {
        // A failure over stale-but-real findings is a different situation from a failure over nothing at all:
        // the first still has data to act on, the second is a blind spot. The number says which.
        var withHistory = Failed("s", "c", "host unreachable", Now);
        withHistory.Findings.Add(new CveFinding { ServerId = "s", CveId = "CVE-2026-1", Package = "openssl" });

        var failed = CveMetrics.FailedTargets([withHistory, Failed("s", "d", "no data", Now)]);

        Assert.Equal(1, failed.Single(f => f.Target == "c").KnownFindings);
        Assert.Equal(0, failed.Single(f => f.Target == "d").KnownFindings);
    }
}
