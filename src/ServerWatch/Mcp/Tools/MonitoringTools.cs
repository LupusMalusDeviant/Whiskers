using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Services.HealthMonitor;
using ServerWatch.Services.Metrics;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Server;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class MonitoringTools
{
    [McpServerTool, Description("Get a health summary of all containers across all servers.")]
    public static async Task<string> GetHealthSummary(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IHealthStore healthStore)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_health_summary");
        if (denied != null) return denied;
        var containers = await docker.ListAllContainersAsync(all: true);
        var running = containers.Count(c => c.State == "running");
        var stopped = containers.Count(c => c.State == "exited");
        var unhealthy = containers.Count(c => c.HealthStatus == "unhealthy");
        var healthy = containers.Count(c => c.HealthStatus == "healthy");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Container Health Summary:");
        sb.AppendLine($"  Total: {containers.Count}");
        sb.AppendLine($"  Running: {running}");
        sb.AppendLine($"  Stopped: {stopped}");
        sb.AppendLine($"  Healthy: {healthy}");
        sb.AppendLine($"  Unhealthy: {unhealthy}");

        if (unhealthy > 0)
        {
            sb.AppendLine($"\nUnhealthy containers:");
            foreach (var c in containers.Where(c => c.HealthStatus == "unhealthy"))
                sb.AppendLine($"  - {c.Name} ({c.Image}) on {c.ServerName}");
        }

        if (stopped > 0)
        {
            sb.AppendLine($"\nStopped containers:");
            foreach (var c in containers.Where(c => c.State == "exited"))
                sb.AppendLine($"  - {c.Name} ({c.Image}) on {c.ServerName} — {c.Status}");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get historical CPU/memory metrics for a container over a time period.")]
    public static async Task<string> GetContainerMetrics(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IMetricsQueryService metrics,
        [Description("Container ID")] string containerId,
        [Description("Server ID")] string serverId,
        [Description("Time period: 1h, 6h, 24h, 7d")] string period = "1h")
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_metrics");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID is required.";
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var validPeriods = new[] { "1h", "6h", "24h", "7d" };
        if (!validPeriods.Contains(period.ToLower()))
            return $"Invalid period '{period}'. Must be one of: 1h, 6h, 24h, 7d.";

        var timespan = ParsePeriod(period);
        var cpu = await metrics.GetContainerCpuHistoryAsync(containerId, serverId, timespan);
        var mem = await metrics.GetContainerMemoryHistoryAsync(containerId, serverId, timespan);

        if (!cpu.Any()) return "No metrics available for this container yet.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Metrics for container {(containerId.Length >= 12 ? containerId[..12] : containerId)} (last {period}):");
        sb.AppendLine($"CPU: avg {cpu.Average(p => p.Value):F1}%, max {cpu.Max(p => p.Value):F1}%, min {cpu.Min(p => p.Value):F1}%");
        if (mem.Any())
            sb.AppendLine($"Memory: avg {mem.Average(p => p.Value):F1}%, max {mem.Max(p => p.Value):F1}%, min {mem.Min(p => p.Value):F1}%");
        sb.AppendLine($"Data points: {cpu.Count}");
        return sb.ToString();
    }

    [McpServerTool, Description("Get historical CPU/memory metrics for a server over a time period.")]
    public static async Task<string> GetServerMetrics(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IMetricsQueryService metrics,
        [Description("Server ID")] string serverId,
        [Description("Time period: 1h, 6h, 24h, 7d")] string period = "1h")
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_server_metrics");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var validPeriods = new[] { "1h", "6h", "24h", "7d" };
        if (!validPeriods.Contains(period.ToLower()))
            return $"Invalid period '{period}'. Must be one of: 1h, 6h, 24h, 7d.";

        var timespan = ParsePeriod(period);
        var cpu = await metrics.GetServerCpuHistoryAsync(serverId, timespan);
        var mem = await metrics.GetServerMemoryHistoryAsync(serverId, timespan);

        if (!cpu.Any()) return "No metrics available for this server yet.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Server metrics (last {period}):");
        sb.AppendLine($"CPU: avg {cpu.Average(p => p.Value):F1}%, max {cpu.Max(p => p.Value):F1}%");
        if (mem.Any())
            sb.AppendLine($"Memory: avg {mem.Average(p => p.Value):F1}%, max {mem.Max(p => p.Value):F1}%");
        return sb.ToString();
    }

    [McpServerTool, Description("Get system logs from a server via journalctl. Can filter by service name.")]
    public static async Task<string> GetServerLogs(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IHostCommandExecutor executor,
        [Description("Server ID")] string serverId,
        [Description("Service name to filter (optional, e.g. 'nginx', 'docker')")] string? serviceName = null,
        [Description("Number of lines (default 100)")] int lines = 100,
        [Description("Priority filter: emerg, alert, crit, err, warning, notice, info, debug (optional)")] string? priority = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_server_logs");
        if (denied != null) return denied;
        var cmd = "journalctl --no-pager";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            // Validate service name
            if (!System.Text.RegularExpressions.Regex.IsMatch(serviceName, @"^[a-zA-Z0-9._@-]+$"))
                return "Error: Invalid service name.";
            cmd += $" -u {serviceName}";
        }
        if (!string.IsNullOrWhiteSpace(priority))
            cmd += $" -p {priority}";
        cmd += $" -n {Math.Clamp(lines, 1, 1000)}";

        var result = await executor.ExecuteAsync(serverId, cmd, TimeSpan.FromSeconds(15));
        return result.Success ? result.Output : $"Error: {result.Error}";
    }

    private static TimeSpan ParsePeriod(string period) => period.ToLower() switch
    {
        "1h" => TimeSpan.FromHours(1),
        "6h" => TimeSpan.FromHours(6),
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1)
    };
}
