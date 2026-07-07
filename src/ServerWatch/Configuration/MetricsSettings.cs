namespace ServerWatch.Configuration;

public class MetricsSettings
{
    public const string SectionName = "Metrics";
    public int CollectionIntervalSeconds { get; set; } = 30;
    public int RetentionDays { get; set; } = 7;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional bearer token required to scrape the Prometheus <c>/metrics</c> endpoint. When null or
    /// empty the endpoint is disabled (opt-in) rather than exposed unauthenticated, because its payload
    /// is the full multi-server container inventory. Set via <c>Metrics__ScrapeToken</c>.
    /// </summary>
    public string? ScrapeToken { get; set; }
}
