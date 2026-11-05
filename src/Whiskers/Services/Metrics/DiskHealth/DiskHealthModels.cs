namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58 (contract 0005): what Whiskers can say about a disk, an array or a host. Neither <see cref="Unknown"/>
/// nor <see cref="Unsupported"/> is ever healthy.</summary>
public enum DiskHealthState
{
    /// <summary>Read good, with collection-age evidence inside the freshness limit.</summary>
    Healthy,

    /// <summary>SMART failed, an md array lost disks, or a ZFS pool is not online.</summary>
    DegradedOrFailing,

    /// <summary>Whiskers cannot vouch for it: the query failed or was malformed, series went missing, or the
    /// collection-age evidence is missing or older than the freshness limit.</summary>
    Unknown,

    /// <summary>The host never had a reading (no metrics endpoint, or none of the three sources).</summary>
    Unsupported,
}

/// <summary>One disk, md array or ZFS pool.</summary>
/// <param name="Kind"><see cref="DiskHealthKinds.Smart"/>, <see cref="DiskHealthKinds.Md"/> or <see cref="DiskHealthKinds.Zfs"/>.</param>
/// <param name="Name">The disk, md device or pool from the series label, display-escaped (<see cref="DiskHealthParser.DisplayName"/>).</param>
/// <param name="State">Its state.</param>
/// <param name="Reason">Why — "SMART overall-health failed", "md0: 1 of 2 disks failed", "smartmon.prom 3 h old".</param>
/// <param name="CollectedUtc">The collection-age evidence used (SMART: the older of the smartctl run and the textfile's
/// mtime), if any. md and ZFS have none: their evidence is the collector's success at query time.</param>
public sealed record DiskHealthItem(
    string Kind,
    string Name,
    DiskHealthState State,
    string Reason,
    DateTime? CollectedUtc);

/// <summary>A host's disk health at <see cref="ReadUtc"/>.</summary>
/// <param name="Overall">The worst item (DegradedOrFailing, then Unknown, then Healthy); Unsupported only when the host
/// never had a reading.</param>
public sealed record DiskHealthReading(
    string ServerId,
    DiskHealthState Overall,
    IReadOnlyList<DiskHealthItem> Items,
    DateTime ReadUtc)
{
    /// <summary>Whether this is a reading at all — anything but <see cref="DiskHealthState.Unsupported"/>.</summary>
    public bool HadReading => Overall != DiskHealthState.Unsupported;
}

/// <summary>The item kinds.</summary>
public static class DiskHealthKinds
{
    public const string Smart = "smart";
    public const string Md = "md";
    public const string Zfs = "zfs";
}

/// <summary>
/// The six fixed instant queries of one host, each selected on <c>server="&lt;ServerConfig.Id&gt;"</c>, and their results.
/// </summary>
public sealed record DiskHealthQueries(
    PromQueryResult Smart,
    PromQueryResult SmartRun,
    PromQueryResult TextfileMtime,
    PromQueryResult Md,
    PromQueryResult Zfs,
    PromQueryResult Collectors)
{
    /// <summary>Every query failed for the same <paramref name="reason"/> (endpoint unreachable, a read that threw).</summary>
    public static DiskHealthQueries AllFailed(string reason)
    {
        var failed = PromQueryResult.Failed(reason);
        return new(failed, failed, failed, failed, failed, failed);
    }

    /// <summary>The server has no metrics endpoint: nothing was asked (a Docker-source server — Unsupported unless it had
    /// a reading before).</summary>
    public static DiskHealthQueries WithoutEndpoint() => AllFailed("no metrics endpoint") with { NoEndpoint = true };

    /// <summary>Set by <see cref="WithoutEndpoint"/>: the failures mean "never asked", not "asked and failed".</summary>
    public bool NoEndpoint { get; init; }

    /// <summary>The PromQL of the six queries for <paramref name="serverId"/>, in the order of the record.</summary>
    public static IReadOnlyList<string> For(string serverId)
    {
        var s = "server=\"" + serverId.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return
        [
            "{__name__=~\"smartmon_device_smart_(healthy|available|enabled)\"," + s + "}",
            "smartmon_smartctl_run{" + s + "}",
            // node_exporter labels the file by name or (newer) by path: both match.
            "node_textfile_mtime_seconds{" + s + ",file=~\"(.*/)?smartmon\\\\.prom\"}",
            "{__name__=~\"node_md_state|node_md_disks|node_md_disks_required\"," + s + "}",
            "node_zfs_zpool_state{" + s + "}",
            "node_scrape_collector_success{" + s + ",collector=~\"mdadm|zfs\"}",
        ];
    }
}
