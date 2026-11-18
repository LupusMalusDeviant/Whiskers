using Whiskers.Models.Cve;

namespace Whiskers.Services.Cve;

/// <summary>What one server's vulnerability picture looks like as numbers.</summary>
/// <param name="ServerId">The server these numbers belong to.</param>
/// <param name="BySeverity">Open findings per severity. Severities with no findings are absent, not zero —
/// a server with nothing critical should not carry a permanent <c>critical 0</c>.</param>
/// <param name="StaleTargets">How many of this server's targets are running on data old enough that a scan
/// must have stopped happening.</param>
/// <param name="OldestDataAge">Age of the least recently scanned target. This is the number that would have
/// caught the Trivy parse failure.</param>
/// <param name="FailedTargets">Targets whose last scan failed. Their finding count is not a verdict — it is
/// the absence of one, and without this number zero findings and zero knowledge look the same.</param>
public sealed record CveServerMetrics(
    string ServerId,
    IReadOnlyList<(CveSeverity Severity, int Count)> BySeverity,
    int StaleTargets,
    TimeSpan OldestDataAge,
    int FailedTargets);

/// <summary>One target whose data is old enough that its scan must have stopped happening.</summary>
/// <param name="Target">Container name, or "host OS" — the thing an operator has to go and look at.</param>
public sealed record CveStaleTarget(string ServerId, string Target, TimeSpan Age);

/// <summary>One target whose last scan failed.</summary>
/// <param name="KnownFindings">What is still on record for it — zero here means nothing is known, which is
/// not the same as nothing being wrong.</param>
public sealed record CveFailedTarget(string ServerId, string Target, string Error, int KnownFindings);

/// <summary>The whole fleet's picture.</summary>
/// <param name="PerServer">One entry per server that has any stored results.</param>
/// <param name="DistinctCveIds">Distinct CVE identifiers across the fleet. The finding count says how much
/// work the display has; this says how many actual problems there are.</param>
public sealed record CveFleetMetrics(
    IReadOnlyList<CveServerMetrics> PerServer,
    int DistinctCveIds);

/// <summary>
/// Turns stored CVE scan results into the numbers <c>/metrics</c> publishes (Plan-0002 follow-up, 2026-08-27).
///
/// <para>The interesting one is <see cref="CveServerMetrics.OldestDataAge"/>. When a scan fails, the monitor
/// deliberately keeps the previous results rather than reporting a false all-clear — which is right, and which
/// means a target whose scanner has been broken for months looks exactly like one that was scanned this
/// morning. That is how an identity-provider image went unscanned from July to late August: the findings were still
/// there, still plausible, and simply never changed. The only visible difference was the age of the data, and
/// nothing was watching it.</para>
///
/// <para>Deliberately free of container labels. The self-metrics endpoint bounds cardinality by server and
/// loop, never by container, because a container label across a large fleet turns a few dozen series into
/// thousands — a monitoring outage caused by monitoring. So staleness is published as a count per server, and
/// naming the guilty target is left to the UI and the MCP tools, which can afford the detail.</para>
/// </summary>
public static class CveMetrics
{
    /// <summary>
    /// How far past its interval a target's data may fall before it counts as stale.
    ///
    /// <para>Two whole intervals, not one. A single failed scan retries within ~15 minutes, so one missed
    /// refresh is a blip that repairs itself; two consecutive missed cycles is a scanner that is not coming
    /// back on its own. At the default 12-hour interval that means a target goes stale after a day — against
    /// the six weeks it actually took, an alarm a day late is not the problem.</para>
    /// </summary>
    public static TimeSpan StaleAfter(TimeSpan scanInterval) => scanInterval * 2;

    /// <summary>
    /// The individual targets running on stale data, worst first — the detail the metrics endpoint cannot
    /// carry.
    ///
    /// <para><c>/metrics</c> counts stale targets per server and deliberately leaves the container out of the
    /// labels, because a container label across a large fleet multiplies the series into the thousands. That
    /// keeps the endpoint cheap and leaves an operator with a number and nowhere to go, so the naming happens
    /// here instead, where the caller asks one question at a time and cardinality costs nothing.</para>
    /// </summary>
    public static IReadOnlyList<CveStaleTarget> StaleTargets(
        IReadOnlyList<CveScanResult> results, TimeSpan scanInterval, DateTime nowUtc)
    {
        var staleAfter = StaleAfter(scanInterval);
        return results
            .Where(r => nowUtc - r.ScannedAt > staleAfter)
            .Select(r => new CveStaleTarget(
                r.ServerId,
                r.Source == CveSource.Os ? "host OS" : r.ContainerName ?? r.ContainerId ?? "(unnamed)",
                Age(r, nowUtc)))
            .OrderByDescending(t => t.Age)
            .ToList();
    }

    /// <summary>
    /// The targets whose last scan failed, worst-sounding first — the ones whose emptiness means "we do not
    /// know", not "nothing found".
    ///
    /// <para>Found on 2026-08-28: two running containers on web-02 could not be scanned because their
    /// local image layers were damaged, and because neither had ever been scanned successfully there was no
    /// result to keep — so they were absent from every list rather than present and failing. A missing target
    /// looks exactly like a clean one. Staleness cannot catch this: what is not there cannot age.</para>
    /// </summary>
    public static IReadOnlyList<CveFailedTarget> FailedTargets(IReadOnlyList<CveScanResult> results)
        => results
            .Where(r => !string.IsNullOrWhiteSpace(r.Error))
            .Select(r => new CveFailedTarget(
                r.ServerId,
                r.Source == CveSource.Os ? "host OS" : r.ContainerName ?? r.ContainerId ?? "(unnamed)",
                r.Error!,
                r.Findings.Count))
            .OrderBy(t => t.ServerId, StringComparer.Ordinal)
            .ThenBy(t => t.Target, StringComparer.Ordinal)
            .ToList();

    public static CveFleetMetrics Build(
        IReadOnlyList<CveScanResult> results, TimeSpan scanInterval, DateTime nowUtc)
    {
        var staleAfter = StaleAfter(scanInterval);

        var perServer = results
            .GroupBy(r => r.ServerId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CveServerMetrics(
                g.Key,
                g.SelectMany(r => r.Findings)
                    .GroupBy(f => f.Severity)
                    .Select(s => (Severity: s.Key, Count: s.Count()))
                    .OrderByDescending(s => s.Severity)
                    .ToList(),
                g.Count(r => nowUtc - r.ScannedAt > staleAfter),
                // Max, not average: one target frozen since July among fifty fresh ones is the whole finding,
                // and an average would bury it under the servers that are working.
                g.Max(r => Age(r, nowUtc)),
                g.Count(r => !string.IsNullOrWhiteSpace(r.Error))))
            .OrderBy(s => s.ServerId, StringComparer.Ordinal)
            .ToList();

        var distinct = results
            .SelectMany(r => r.Findings)
            .Select(f => f.CveId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new CveFleetMetrics(perServer, distinct);
    }

    // A scan timestamp in the future is a clock that moved, not data from tomorrow. Reporting a negative age
    // would render as a negative gauge and read as "scanned recently" — the opposite of the truth if the clock
    // then settles.
    private static TimeSpan Age(CveScanResult result, DateTime nowUtc)
    {
        var age = nowUtc - result.ScannedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
