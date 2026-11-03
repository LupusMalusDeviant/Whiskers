namespace Whiskers.Configuration;

public class MetricsSettings
{
    public const string SectionName = "Metrics";
    public int CollectionIntervalSeconds { get; set; } = 30;
    /// <summary>R1-55: the raw tier's retention only, and not read from here — <see cref="MetricRetention"/> resolves it
    /// (with the roll-up tiers' and the alert history's retention) from the configuration layers; a value set in the
    /// environment or appsettings.json is read-only on the Settings page.</summary>
    public int RetentionDays { get; set; } = 7;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional bearer token required to scrape the Prometheus <c>/metrics</c> endpoint. When null or
    /// empty the endpoint is disabled (opt-in) rather than exposed unauthenticated, because its payload
    /// is the full multi-server container inventory. Set via <c>Metrics__ScrapeToken</c>.
    /// </summary>
    public string? ScrapeToken { get; set; }
}
