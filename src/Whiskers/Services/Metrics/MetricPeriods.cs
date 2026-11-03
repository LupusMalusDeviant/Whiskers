namespace Whiskers.Services.Metrics;

/// <summary>The history ranges the charts and the monitoring MCP tools offer. R1-55 added <c>30d</c>, which the coarser
/// tiers keep after the raw samples are gone.</summary>
public static class MetricPeriods
{
    public static IReadOnlyList<string> All { get; } = ["1h", "6h", "24h", "7d", "30d"];

    /// <summary>The range of a period name, or null for an unknown one.</summary>
    public static TimeSpan? Parse(string? period) => period?.Trim().ToLowerInvariant() switch
    {
        "1h" => TimeSpan.FromHours(1),
        "6h" => TimeSpan.FromHours(6),
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => null,
    };
}
