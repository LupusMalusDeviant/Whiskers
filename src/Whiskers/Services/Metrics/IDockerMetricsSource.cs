using Whiskers.Models;

namespace Whiskers.Services.Metrics;

/// <summary>Reads live metrics straight from the Docker engine (default source).</summary>
public interface IDockerMetricsSource
{
    Task<ContainerStats?> GetContainerStatsAsync(string? serverId, string containerId, string containerName);
    Task<ServerSystemInfo> GetServerSystemInfoAsync(string serverId);
}
