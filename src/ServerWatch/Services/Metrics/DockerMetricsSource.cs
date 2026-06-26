using ServerWatch.Models;
using ServerWatch.Services.Docker;

namespace ServerWatch.Services.Metrics;

/// <summary>
/// Legacy metrics source: delegates to <see cref="IDockerService"/> (container stats via Docker
/// API, host metrics via SSH /proc-exec). This is the default and the fallback — behaviour is
/// unchanged from before the <see cref="IMetricsSource"/> seam was introduced.
/// </summary>
public class DockerMetricsSource
{
    private readonly IDockerService _docker;

    public DockerMetricsSource(IDockerService docker) => _docker = docker;

    public Task<ContainerStats?> GetContainerStatsAsync(string? serverId, string containerId, string containerName)
        => _docker.GetContainerStatsAsync(containerId, serverId);

    public Task<ServerSystemInfo> GetServerSystemInfoAsync(string serverId)
        => _docker.GetServerSystemInfoAsync(serverId);
}
