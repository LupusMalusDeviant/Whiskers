namespace ServerWatch.Configuration;

/// <summary>Thresholds for metric-based events (high CPU/RAM, anomalies) emitted by the metrics
/// collector. These flow through the notification pipeline and can drive AI triggers.</summary>
public class MetricAlertSettings
{
    public const string SectionName = "MetricAlert";

    public bool Enabled { get; set; } = true;

    /// <summary>Container CPU% that counts as "high".</summary>
    public double CpuPercent { get; set; } = 90;

    /// <summary>Container memory% (of its limit) that counts as "high".</summary>
    public double MemoryPercent { get; set; } = 90;

    /// <summary>How long the value must stay above the threshold before an event fires.</summary>
    public int SustainedMinutes { get; set; } = 3;

    /// <summary>Minimum minutes between repeated events for the same container/metric.</summary>
    public int CooldownMinutes { get; set; } = 15;

    // --- Simple anomaly (outlier) detection ---

    public bool AnomalyEnabled { get; set; } = false;

    /// <summary>Number of recent samples used as the rolling baseline.</summary>
    public int AnomalyWindow { get; set; } = 20;

    /// <summary>Standard deviations above the rolling mean that counts as an anomaly.</summary>
    public double AnomalySigma { get; set; } = 3;

    /// <summary>Ignore anomalies below this absolute value (avoids noise on near-idle containers).</summary>
    public double AnomalyFloorPercent { get; set; } = 40;
}
