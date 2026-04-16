namespace ServerWatch.Configuration;

public class DockerSettings
{
    public const string SectionName = "Docker";
    public string SocketPath { get; set; } = "unix:///var/run/docker.sock";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int StatsPollingIntervalSeconds { get; set; } = 3;
}
