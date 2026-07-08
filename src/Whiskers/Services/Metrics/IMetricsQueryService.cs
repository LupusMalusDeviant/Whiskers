using Whiskers.Models;

namespace Whiskers.Services.Metrics;

/// <summary>Queries historical container/server metrics from the time-series store.</summary>
public interface IMetricsQueryService
{
    Task<List<MetricPoint>> GetContainerCpuHistoryAsync(string containerId, string serverId, TimeSpan period);
    Task<List<MetricPoint>> GetContainerMemoryHistoryAsync(string containerId, string serverId, TimeSpan period);
    Task<List<MetricPoint>> GetServerCpuHistoryAsync(string serverId, TimeSpan period);
    Task<List<MetricPoint>> GetServerMemoryHistoryAsync(string serverId, TimeSpan period);
}
