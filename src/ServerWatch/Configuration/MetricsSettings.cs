namespace ServerWatch.Configuration;

public class MetricsSettings
{
    public const string SectionName = "Metrics";
    public int CollectionIntervalSeconds { get; set; } = 30;
    public int RetentionDays { get; set; } = 7;
    public bool Enabled { get; set; } = true;
}
