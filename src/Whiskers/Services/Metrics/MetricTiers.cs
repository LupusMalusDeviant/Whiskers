namespace Whiskers.Services.Metrics;

/// <summary>
/// R1-55: the fixed roll-up tiers. Raw samples (every collection interval) roll up into 10-minute windows, those into
/// 1-hour windows, those into 8-hour windows; each window keeps the sample-weighted average and the true maximum of every
/// series and its sample count. Windows are aligned to their length since the epoch, so every coarser window is made of
/// whole finer ones and two tiers can be stitched at any coarser boundary.
/// </summary>
public static class MetricTiers
{
    /// <summary>The roll-up tiers by window length in minutes, finest first. Level 0 is the raw data.</summary>
    public static IReadOnlyList<int> Minutes { get; } = [10, 60, 480];

    public static TimeSpan Size(int minutes) => TimeSpan.FromMinutes(minutes);

    /// <summary>Start of the window of length <paramref name="size"/> that holds <paramref name="t"/>.</summary>
    public static DateTime Floor(DateTime t, TimeSpan size) => new(t.Ticks - t.Ticks % size.Ticks, DateTimeKind.Utc);

    /// <summary>The first window boundary at or after <paramref name="t"/>.</summary>
    public static DateTime Ceiling(DateTime t, TimeSpan size)
    {
        var floor = Floor(t, size);
        return floor.Ticks == t.Ticks ? floor : floor + size;
    }

    /// <summary>The key of a tier's watermark row in <c>MetricRollupStates</c>.</summary>
    public static string StateId(string kind, int minutes) => $"{kind}/{minutes}";

    public const string ContainerKind = "container";
    public const string ServerKind = "server";
}
