using ServerWatch.Models;

namespace ServerWatch.Services.Metrics;

/// <summary>Reads server metrics from a Prometheus/VictoriaMetrics endpoint (push/scrape source).</summary>
public interface IPrometheusMetricsSource
{
    Task<ServerSystemInfo?> GetServerSystemInfoAsync(string serverId);
}
