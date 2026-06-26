using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Metrics;

/// <summary>
/// Routes each metrics read to the source configured for that server
/// (<see cref="ServerConfig.MetricsSource"/>). Docker is the default and fallback; Prometheus is
/// opt-in per server. Mirrors the parallel fan-out of the legacy
/// <c>DockerService.GetAllServerSystemInfoAsync</c> so Docker servers behave identically.
/// </summary>
public class MetricsSourceDispatcher : IMetricsSource
{
    private readonly ServerConfigService _serverConfig;
    private readonly DockerMetricsSource _docker;
    private readonly PrometheusMetricsSource _prometheus;
    private readonly ILogger<MetricsSourceDispatcher> _logger;

    public MetricsSourceDispatcher(
        ServerConfigService serverConfig,
        DockerMetricsSource docker,
        PrometheusMetricsSource prometheus,
        ILogger<MetricsSourceDispatcher> logger)
    {
        _serverConfig = serverConfig;
        _docker = docker;
        _prometheus = prometheus;
        _logger = logger;
    }

    // Container stats always come from the Docker API (exact per-container values). Step 1 only
    // moves HOST metrics off SSH; the Docker path itself is de-SSH'd in Step 2 (mTLS). cAdvisor
    // was dropped — its container discovery is unreliable on cgroup-v2 hosts and it's redundant
    // with the Docker stats API.
    public Task<ContainerStats?> GetContainerStatsAsync(string? serverId, string containerId, string containerName)
        => _docker.GetContainerStatsAsync(serverId, containerId, containerName);

    public async Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync()
    {
        var servers = _serverConfig.GetEnabledServers();
        var tasks = servers.Select(async s =>
        {
            try
            {
                ServerSystemInfo? info = s.MetricsSource == MetricsSourceKind.Prometheus
                    ? await _prometheus.GetServerSystemInfoAsync(s.Id)
                    : await _docker.GetServerSystemInfoAsync(s.Id);
                return (s.Id, info);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server system info failed for {ServerId} via {Source}", s.Id, s.MetricsSource);
                return (s.Id, (ServerSystemInfo?)null);
            }
        });

        var results = await Task.WhenAll(tasks);
        return results
            .Where(r => r.Item2 != null)
            .ToDictionary(r => r.Id, r => r.Item2!);
    }
}
