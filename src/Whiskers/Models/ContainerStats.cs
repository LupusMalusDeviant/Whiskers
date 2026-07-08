namespace Whiskers.Models;

public class ContainerStats
{
    public string ContainerId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double MemoryPercent => MemoryLimitBytes > 0
        ? (double)MemoryUsageBytes / MemoryLimitBytes * 100 : 0;
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public long BlockReadBytes { get; set; }
    public long BlockWriteBytes { get; set; }
}
