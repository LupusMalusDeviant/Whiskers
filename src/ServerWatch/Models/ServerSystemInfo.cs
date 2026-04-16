namespace ServerWatch.Models;

public class ServerSystemInfo
{
    public string ServerId { get; set; } = "local";
    public string ServerName { get; set; } = "Local";

    // OS Info
    public string OperatingSystem { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string KernelVersion { get; set; } = "";
    public string Architecture { get; set; } = "";

    // Resources
    public int CpuCount { get; set; }
    public long MemoryTotalBytes { get; set; }
    public long MemoryUsedBytes { get; set; }
    public double MemoryUsedPercent => MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes * 100 : 0;
    public double CpuUsagePercent { get; set; }

    // Docker Info
    public string DockerVersion { get; set; } = "";
    public int ContainersRunning { get; set; }
    public int ContainersStopped { get; set; }
    public int ContainersTotal { get; set; }
    public int ImagesCount { get; set; }

    // Network
    public string IpAddress { get; set; } = "";
    public List<string> ListeningPorts { get; set; } = new();

    // Status
    public bool IsReachable { get; set; }
    public string? Error { get; set; }
}
