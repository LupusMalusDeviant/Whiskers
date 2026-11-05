using System.Globalization;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>
/// R1-58 (contract 0005): turns the six query results of one host into a <see cref="DiskHealthReading"/>. Pure — no
/// clock, no I/O — and fixture-tested (<c>DiskHealthReadingTests</c>).
///
/// <para><b>Freshness.</b> The query's sample time never counts: a stale textfile is still scraped with a fresh sample
/// time. SMART is fresh only while both <c>smartmon_smartctl_run</c> (the run's unix time) and
/// <c>node_textfile_mtime_seconds</c> of <c>smartmon.prom</c> are within the freshness limit; md and ZFS only while their
/// collector reports success (<c>node_scrape_collector_success</c> = 1) in the same query time — their series exist only
/// while they are scraped.</para>
///
/// <para><b>Precedence.</b> A value that reads bad is DegradedOrFailing even when its evidence is old or incomplete — a
/// failed disk is never shown as less than failing for want of a fresh timestamp. Otherwise missing or old evidence,
/// a value that is no clear good and anything that went missing since the last reading is Unknown. Only a clear good
/// value with fresh evidence is Healthy.</para>
///
/// <para><b>Missing and partial.</b> Every item of the previous reading that the current one lacks — its query failed,
/// its series are gone — is carried as Unknown (reason "no longer reported" or the query's failure). A host without any
/// item is Unknown when it had a reading before, Unsupported when it never had one.</para>
/// </summary>
public static class DiskHealthParser
{
    /// <summary>The documented freshness limit of SMART textfile data (decision 2): the script runs every 15 minutes.</summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(2);

    /// <summary>How far a collection time may lie ahead of Whiskers' clock before it is distrusted.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The reason prefix of an item carried from the previous reading.</summary>
    public const string NoLongerReported = "no longer reported";

    /// <summary>The name of the line that stands for a source whose query failed (no disk, array or pool of its own).</summary>
    public const string FailedQueryName = "(query failed)";

    private const int MaxNameLength = 64;

    public static DiskHealthReading Parse(string serverId, DiskHealthQueries results, DateTime nowUtc,
        DiskHealthReading? previous, TimeSpan freshness)
    {
        ArgumentNullException.ThrowIfNull(results);
        var items = new List<DiskHealthItem>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);

        var smartFailure = FirstError(results.Smart, results.SmartRun, results.TextfileMtime);
        if (smartFailure is not null) failed[DiskHealthKinds.Smart] = smartFailure;
        else items.AddRange(Smart(results, nowUtc, freshness));

        var collectors = results.Collectors.Success ? results.Collectors.Samples : null;
        var collectorsError = results.Collectors.Error;
        if (!results.Md.Success) failed[DiskHealthKinds.Md] = results.Md.Error ?? "query failed";
        else items.AddRange(Md(results.Md.Samples, Evidence(collectors, collectorsError, "mdadm")));

        if (!results.Zfs.Success) failed[DiskHealthKinds.Zfs] = results.Zfs.Error ?? "query failed";
        else items.AddRange(Zfs(results.Zfs.Samples, Evidence(collectors, collectorsError, "zfs")));

        // What the last reading had and this one lacks is Unknown — never silently dropped, never healthy.
        if (previous is { HadReading: true })
        {
            var present = items.Select(Key).ToHashSet(StringComparer.Ordinal);
            foreach (var before in previous.Items)
            {
                // A failed query's own line is no disk: it is said again below while the query still fails, else gone.
                if (before.Name == FailedQueryName) continue;
                if (!present.Add(Key(before))) continue;
                var why = failed.TryGetValue(before.Kind, out var error) ? $"query failed: {error}" : NoLongerReported;
                // The first reason of a carried item stays: "no longer reported — last: SMART overall-health failed".
                var last = before.Reason.StartsWith(NoLongerReported, StringComparison.Ordinal)
                           || before.Reason.StartsWith("query failed", StringComparison.Ordinal)
                    ? LastOf(before.Reason)
                    : before.Reason;
                items.Add(before with { State = DiskHealthState.Unknown, Reason = $"{why} — last: {last}" });
            }
        }

        // A source whose query failed and that has no line of its own (nothing was carried for it) still keeps the host
        // from reading Healthy — as long as the host has anything to show at all; a host without any line is
        // Unsupported (never read) or Unknown (read before) as below.
        if (items.Count > 0)
            foreach (var (kind, error) in failed)
                if (!items.Any(i => i.Kind == kind))
                    items.Add(new DiskHealthItem(kind, FailedQueryName, DiskHealthState.Unknown, $"query failed: {error}", null));

        var ordered = items
            .OrderBy(i => KindOrder(i.Kind)).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
        DiskHealthState overall;
        if (ordered.Count == 0)
            overall = previous is { HadReading: true } ? DiskHealthState.Unknown : DiskHealthState.Unsupported;
        else if (ordered.Any(i => i.State == DiskHealthState.DegradedOrFailing))
            overall = DiskHealthState.DegradedOrFailing;
        else if (ordered.Any(i => i.State != DiskHealthState.Healthy))
            overall = DiskHealthState.Unknown;
        else
            overall = DiskHealthState.Healthy;
        return new DiskHealthReading(serverId, overall, ordered, nowUtc);
    }

    // ------------------------------------------------------------------------------------------------- SMART

    private static IEnumerable<DiskHealthItem> Smart(DiskHealthQueries results, DateTime now, TimeSpan freshness)
    {
        var values = new Dictionary<string, (string Name, Dictionary<string, double> Values)>(StringComparer.Ordinal);
        (string Name, Dictionary<string, double> Values) Of(PromSample s)
        {
            var disk = s.Label("disk") ?? "";
            var type = s.Label("type");
            var key = disk + "\n" + type;
            if (!values.TryGetValue(key, out var entry))
            {
                // A controller-addressed disk (megaraid,0 / cciss,1) shares its device path with its siblings.
                var raw = type is not null && type.Contains(',') ? $"{disk} [{type}]" : disk;
                values[key] = entry = (DisplayName(raw), new Dictionary<string, double>(StringComparer.Ordinal));
            }
            return entry;
        }

        foreach (var s in results.Smart.Samples)
            Of(s).Values[s.Label("__name__") ?? "?"] = s.Value;
        foreach (var s in results.SmartRun.Samples)
            Of(s).Values["run"] = s.Value;

        DateTime? mtime = results.TextfileMtime.Samples.Count == 0
            ? null
            : FromUnixOrNull(results.TextfileMtime.Samples.Max(s => s.Value));

        if (values.Count == 0)
        {
            // The file is there (it has an mtime) but holds no disk: the script ran into a problem.
            if (mtime is not null)
                yield return new DiskHealthItem(DiskHealthKinds.Smart, "smartmon.prom", DiskHealthState.Unknown,
                    "smartmon.prom holds no SMART readings", mtime);
            yield break;
        }

        foreach (var (name, v) in values.Values)
        {
            DateTime? run = v.TryGetValue("run", out var r) ? FromUnixOrNull(r) : null;
            var stale = Staleness(run, mtime, now, freshness);
            DateTime? collected = run is { } a && mtime is { } b ? (a < b ? a : b) : run ?? mtime;
            v.TryGetValue("smartmon_device_smart_healthy", out var healthy);
            var hasHealthy = v.ContainsKey("smartmon_device_smart_healthy");

            DiskHealthItem Item(DiskHealthState state, string reason) =>
                new(DiskHealthKinds.Smart, name, state, reason, collected);

            if (hasHealthy && healthy == 0)
                yield return Item(DiskHealthState.DegradedOrFailing,
                    "SMART overall-health failed" + (stale is null ? "" : $" ({stale})"));
            else if (v.TryGetValue("smartmon_device_smart_available", out var available) && available != 1)
                yield return Item(DiskHealthState.Unknown, "SMART is not available on this disk");
            else if (v.TryGetValue("smartmon_device_smart_enabled", out var enabled) && enabled != 1)
                yield return Item(DiskHealthState.Unknown, "SMART is not enabled on this disk");
            else if (!hasHealthy)
                yield return Item(DiskHealthState.Unknown, "no SMART overall-health value");
            else if (healthy != 1)
                yield return Item(DiskHealthState.Unknown, "unexpected SMART overall-health value " + Number(healthy));
            else if (stale is not null)
                yield return Item(DiskHealthState.Unknown, stale);
            else
                yield return Item(DiskHealthState.Healthy, "SMART overall-health passed");
        }
    }

    /// <summary>Why the SMART evidence is not fresh, or null when both times are within the limit.</summary>
    private static string? Staleness(DateTime? run, DateTime? mtime, DateTime now, TimeSpan freshness)
    {
        if (run is null) return "no smartctl run time";
        if (mtime is null) return "no modification time for smartmon.prom";
        if (run.Value - now > ClockSkew || mtime.Value - now > ClockSkew) return "collection time lies in the future";
        if (now - mtime.Value > freshness) return $"smartmon.prom {Age(now - mtime.Value)} old";
        if (now - run.Value > freshness) return $"smartctl last ran {Age(now - run.Value)} ago";
        return null;
    }

    // ------------------------------------------------------------------------------------------------- md

    private static IEnumerable<DiskHealthItem> Md(IReadOnlyList<PromSample> samples, string? evidenceProblem)
    {
        foreach (var device in samples.GroupBy(s => s.Label("device") ?? "", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var name = DisplayName(device.Key);
            double? required = null, active = null, failedDisks = null;
            string? state = null;
            foreach (var s in device)
            {
                switch (s.Label("__name__"))
                {
                    case "node_md_disks_required": required = s.Value; break;
                    case "node_md_disks" when s.Label("state") == "active": active = s.Value; break;
                    case "node_md_disks" when s.Label("state") == "failed": failedDisks = s.Value; break;
                    case "node_md_state" when s.Value == 1: state = DisplayName(s.Label("state") ?? "?"); break;
                }
            }

            DiskHealthItem Item(DiskHealthState st, string reason) => new(DiskHealthKinds.Md, name, st, reason, null);

            if (failedDisks is > 0.0)
                yield return Item(DiskHealthState.DegradedOrFailing,
                    $"{name}: {Number(failedDisks.Value)} of {(required is { } req ? Number(req) : "?")} disks failed");
            else if (required is { } need && active is { } have && have < need)
                yield return Item(DiskHealthState.DegradedOrFailing, $"{name}: {Number(have)} of {Number(need)} disks active");
            else if (required is null || active is null || double.IsNaN(required.Value) || double.IsNaN(active.Value))
                yield return Item(DiskHealthState.Unknown, $"{name}: incomplete md readings");
            else if (state == "inactive")
                yield return Item(DiskHealthState.Unknown, $"{name}: array inactive");
            else if (evidenceProblem is not null)
                yield return Item(DiskHealthState.Unknown, evidenceProblem);
            else
                yield return Item(DiskHealthState.Healthy,
                    $"{name}: {Number(active.Value)} of {Number(required.Value)} disks active" +
                    (state is null or "active" ? "" : $" ({state})"));
        }
    }

    // ------------------------------------------------------------------------------------------------- ZFS

    private static IEnumerable<DiskHealthItem> Zfs(IReadOnlyList<PromSample> samples, string? evidenceProblem)
    {
        foreach (var pool in samples.GroupBy(s => s.Label("zpool") ?? "", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var name = DisplayName(pool.Key);
            var current = pool.Where(s => s.Value == 1).Select(s => s.Label("state") ?? "").Distinct(StringComparer.Ordinal).ToList();

            DiskHealthItem Item(DiskHealthState st, string reason) => new(DiskHealthKinds.Zfs, name, st, reason, null);

            if (current.Count != 1)
                yield return Item(DiskHealthState.Unknown, "pool state cannot be read");
            else if (!string.Equals(current[0], "online", StringComparison.OrdinalIgnoreCase))
                yield return Item(DiskHealthState.DegradedOrFailing, $"pool state {DisplayName(current[0])}");
            else if (evidenceProblem is not null)
                yield return Item(DiskHealthState.Unknown, evidenceProblem);
            else
                yield return Item(DiskHealthState.Healthy, "pool state online");
        }
    }

    // ------------------------------------------------------------------------------------------------- helpers

    /// <summary>Why the collector's success is not proven at query time, or null when it reported 1.</summary>
    private static string? Evidence(IReadOnlyList<PromSample>? collectors, string? error, string collector)
    {
        if (collectors is null) return $"{collector} collector status could not be read ({error ?? "query failed"})";
        var sample = collectors.FirstOrDefault(s => s.Label("collector") == collector);
        if (sample is null) return $"no {collector} collector status";
        return sample.Value == 1 ? null : $"{collector} collector did not succeed";
    }

    private static string? FirstError(params PromQueryResult[] results) =>
        results.FirstOrDefault(r => !r.Success) is { } f ? f.Error ?? "query failed" : null;

    private static string Key(DiskHealthItem item) => item.Kind + "\n" + item.Name;

    private static int KindOrder(string kind) => kind switch
    {
        DiskHealthKinds.Smart => 0,
        DiskHealthKinds.Md => 1,
        DiskHealthKinds.Zfs => 2,
        _ => 3,
    };

    private static string LastOf(string reason)
    {
        var at = reason.IndexOf(" — last: ", StringComparison.Ordinal);
        return at < 0 ? reason : reason[(at + " — last: ".Length)..];
    }

    private static DateTime? FromUnixOrNull(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > 253402300799) return null;
        return PromQueryResult.FromUnix(seconds);
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>An age as the reasons state it: "40 min", "3 h", "2 d".</summary>
    public static string Age(TimeSpan span) => span switch
    {
        { TotalMinutes: < 90 } => $"{Math.Max(0, (int)span.TotalMinutes)} min",
        { TotalHours: < 48 } => $"{(int)Math.Round(span.TotalHours)} h",
        _ => $"{(int)Math.Round(span.TotalDays)} d",
    };

    /// <summary>A label value as it may be shown and sent: no control characters, at most 64 characters, never empty.
    /// The value comes from the host; the UI escapes it again when it renders it.</summary>
    public static string DisplayName(string raw)
    {
        var clean = new string((raw ?? "").Where(c => !char.IsControl(c) && c is not ('​' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‪' or '‫' or '‬' or '‭')).ToArray()).Trim();
        if (clean.Length == 0) return "?";
        return clean.Length <= MaxNameLength ? clean : clean[..(MaxNameLength - 1)] + "…";
    }
}
