namespace Whiskers.Configuration;

/// <summary>
/// R1-55: how long each metric tier and the alert history are kept, in days. Raw samples are kept briefly; the coarser
/// tiers (10-minute, 1-hour and 8-hour windows, each with average and peak) keep the long view, so raw retention can
/// shrink without losing it. Retention never deletes a window that a coarser tier has not taken over yet (see
/// <c>MetricRollupService</c>), so a retention shorter than a tier's successor's lag only waits.
/// <para>The keys live in the <c>Metrics</c> section (<see cref="Keys"/>). A key set by the environment
/// (<c>Metrics__RetentionDays</c>, …) or by <c>appsettings.json</c> is read-only; the others are edited on the Settings
/// page, which writes them to the data directory's <c>app-settings.json</c> as a gated, audited <c>config.save</c>.</para>
/// </summary>
public sealed record MetricRetention(int RawDays, int TenMinuteDays, int HourDays, int EightHourDays, int AlertHistoryDays)
{
    /// <summary>Raw 7 days (unchanged from before R1-55), 10-minute windows 30 days, 1-hour windows 180 days, 8-hour
    /// windows 2 years, alert history 30 days.</summary>
    public static MetricRetention Default { get; } = new(7, 30, 180, 730, 30);

    public const int MinDays = 1;
    public const int MaxRawDays = 365;
    public const int MaxDays = 3650;

    /// <summary>The configuration key of each field — the existing <c>Metrics:RetentionDays</c> stays the raw one.</summary>
    public static IReadOnlyList<MetricRetentionField> Keys { get; } =
    [
        new(nameof(RawDays), "Metrics:RetentionDays", MaxRawDays),
        new(nameof(TenMinuteDays), "Metrics:Rollup10mRetentionDays", MaxDays),
        new(nameof(HourDays), "Metrics:Rollup1hRetentionDays", MaxDays),
        new(nameof(EightHourDays), "Metrics:Rollup8hRetentionDays", MaxDays),
        new(nameof(AlertHistoryDays), "Metrics:AlertHistoryRetentionDays", MaxDays),
    ];

    public int Get(string field) => field switch
    {
        nameof(RawDays) => RawDays,
        nameof(TenMinuteDays) => TenMinuteDays,
        nameof(HourDays) => HourDays,
        nameof(EightHourDays) => EightHourDays,
        nameof(AlertHistoryDays) => AlertHistoryDays,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public MetricRetention With(string field, int days) => field switch
    {
        nameof(RawDays) => this with { RawDays = days },
        nameof(TenMinuteDays) => this with { TenMinuteDays = days },
        nameof(HourDays) => this with { HourDays = days },
        nameof(EightHourDays) => this with { EightHourDays = days },
        nameof(AlertHistoryDays) => this with { AlertHistoryDays = days },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    /// <summary>The retention of a roll-up tier by its window length in minutes.</summary>
    public int ForTier(int tierMinutes) => tierMinutes switch
    {
        10 => TenMinuteDays,
        60 => HourDays,
        480 => EightHourDays,
        _ => throw new ArgumentOutOfRangeException(nameof(tierMinutes), tierMinutes, null),
    };
}

/// <summary>One retention setting: the <see cref="MetricRetention"/> property, its configuration key and its upper
/// bound (the lower bound is always <see cref="MetricRetention.MinDays"/>).</summary>
public sealed record MetricRetentionField(string Field, string Key, int MaxDays)
{
    /// <summary>The environment variable spelling, e.g. <c>Metrics__RetentionDays</c>.</summary>
    public string EnvironmentName => Key.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>The property name inside the <c>Metrics</c> section.</summary>
    public string Property => Key[(Key.IndexOf(':') + 1)..];
}
