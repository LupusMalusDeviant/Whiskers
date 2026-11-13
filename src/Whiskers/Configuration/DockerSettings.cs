namespace Whiskers.Configuration;

public class DockerSettings
{
    public const string SectionName = "Docker";

    /// <summary>Where the hardened profile's socket proxy listens (docker-compose.hardened.yml). Up to 1.0.0 it was
    /// published on <see cref="LegacyHardenedProxyPort"/> of 127.0.0.1 instead.</summary>
    public const string HardenedProxySocketPath = "unix:///var/run/whiskers-proxy/docker.sock";
    public const int LegacyHardenedProxyPort = 2375;

    public string SocketPath { get; set; } = "unix:///var/run/docker.sock";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int StatsPollingIntervalSeconds { get; set; } = 3;
}
