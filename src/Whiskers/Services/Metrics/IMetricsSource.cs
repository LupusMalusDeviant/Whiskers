using Whiskers.Models;

namespace Whiskers.Services.Metrics;

/// <summary>
/// Abstraction over where the metrics collector reads from. Decouples the collector from SSH/Docker
/// so a server can be switched to a push/scrape telemetry pipeline (VictoriaMetrics) without the
/// standing SSH key being on the metrics hot path. The implementation
/// (<see cref="MetricsSourceDispatcher"/>) picks the concrete source per server from its
/// <see cref="ServerConfig.MetricsSource"/>.
/// </summary>
public interface IMetricsSource
{
    /// <summary>Container resource stats, or null if unavailable for this container/server.</summary>
    Task<ContainerStats?> GetContainerStatsAsync(string? serverId, string containerId, string containerName);

    /// <summary>Per-server host system info, keyed by server id (enabled servers only).</summary>
    Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync();
}
