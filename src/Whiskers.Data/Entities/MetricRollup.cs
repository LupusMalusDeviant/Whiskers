namespace Whiskers.Services.Persistence;

/// <summary>
/// R1-55: one closed window of one container's samples, rolled up into a coarser tier (10 min, 1 h or 8 h). Each series
/// keeps its sample-weighted average and its true maximum, so a short spike survives every roll-up and a long range
/// still shows it. <see cref="SampleCount"/> is the number of raw samples behind the window — the weight an average
/// over several windows needs. One row per (tier, server, container, window): the unique index makes a roll-up that is
/// run again for the same window replace its rows rather than add a second set.
/// </summary>
public class ContainerMetricRollupEntity
{
    public long Id { get; set; }

    /// <summary>The tier's window length in minutes (10, 60 or 480).</summary>
    public int TierMinutes { get; set; }

    public string ServerId { get; set; } = "local";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";

    /// <summary>Start of the window (UTC, aligned to the tier's length since the Unix epoch).</summary>
    public DateTime WindowStart { get; set; }

    public int SampleCount { get; set; }
    public double CpuAvg { get; set; }
    public double CpuMax { get; set; }

    /// <summary>Memory use in percent of the container's limit — the value the history charts show.</summary>
    public double MemoryPercentAvg { get; set; }
    public double MemoryPercentMax { get; set; }
    public double MemoryUsageBytesAvg { get; set; }
    public double MemoryUsageBytesMax { get; set; }
}

/// <summary>R1-55: one closed window of one server's samples in a coarser tier — see
/// <see cref="ContainerMetricRollupEntity"/>.</summary>
public class ServerMetricRollupEntity
{
    public long Id { get; set; }
    public int TierMinutes { get; set; }
    public string ServerId { get; set; } = "local";
    public string ServerName { get; set; } = "";
    public DateTime WindowStart { get; set; }
    public int SampleCount { get; set; }
    public double CpuAvg { get; set; }
    public double CpuMax { get; set; }
    public double MemoryPercentAvg { get; set; }
    public double MemoryPercentMax { get; set; }
    public double MemoryUsedBytesAvg { get; set; }
    public double MemoryUsedBytesMax { get; set; }
}

/// <summary>
/// R1-55: how far a tier is rolled up — every window that ends at or before <see cref="RolledUpTo"/> is committed. It is
/// written in the same transaction as the windows themselves, so after a crash it never claims a window that is not
/// there, and retention deletes nothing of the finer tier that a committed window of this one does not cover yet.
/// </summary>
public class MetricRollupStateEntity
{
    /// <summary><c>{kind}/{tierMinutes}</c>, e.g. <c>container/10</c>.</summary>
    public string Id { get; set; } = "";

    public DateTime RolledUpTo { get; set; }
}
