namespace Whiskers.Configuration;

public class HealthMonitorSettings
{
    public const string SectionName = "HealthMonitor";
    public int CheckIntervalSeconds { get; set; } = 30;
    public int HistoryRetentionHours { get; set; } = 24;
    public int RestartLoopThreshold { get; set; } = 5;
    public int RestartLoopWindowMinutes { get; set; } = 10;
}
