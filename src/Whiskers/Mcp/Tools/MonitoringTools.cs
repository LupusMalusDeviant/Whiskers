using ModelContextProtocol.Server;
using Whiskers.Models;
using System.ComponentModel;
using Whiskers.Services.HealthMonitor;
using Whiskers.Services.Metrics;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;
using Whiskers.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class MonitoringTools
{
    private static readonly System.Text.RegularExpressions.Regex ServiceNameRegex = new(@"^[a-zA-Z0-9._@-]+\z");

    // journalctl's own vocabulary and nothing else. The value is appended to a shell line that runs as root on
    // the host, and this tool is Read-level: an unchecked priority was command execution for any read key.
    // \z, not $: in .NET "$" also matches before a trailing newline, and a newline ends the shell command.
    private static readonly System.Text.RegularExpressions.Regex PriorityRegex = new(
        @"^(emerg|alert|crit|err|warning|notice|info|debug|[0-7])(\.\.(emerg|alert|crit|err|warning|notice|info|debug|[0-7]))?\z");

    /// <summary>Builds the journalctl line. Callers must have validated <paramref name="serviceName"/> and
    /// <paramref name="priority"/>; this throws rather than build an unsafe line if they did not.</summary>
    internal static string BuildJournalctlCommand(string? serviceName, int lines, string? priority)
    {
        var cmd = "journalctl --no-pager";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            if (!ServiceNameRegex.IsMatch(serviceName)) throw new ArgumentException("Invalid service name.", nameof(serviceName));
            cmd += $" -u {serviceName}";
        }
        if (!string.IsNullOrWhiteSpace(priority))
        {
            if (!PriorityRegex.IsMatch(priority)) throw new ArgumentException("Invalid priority.", nameof(priority));
            cmd += $" -p {priority}";
        }
        return cmd + $" -n {Math.Clamp(lines, 1, 1000)}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
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

    [McpToolLevel(McpPermissionLevels.Read)]
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

    [McpToolLevel(McpPermissionLevels.Read)]
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

    [McpToolLevel(McpPermissionLevels.Read)]
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
        if (!string.IsNullOrWhiteSpace(serviceName) && !ServiceNameRegex.IsMatch(serviceName))
            return "Error: Invalid service name.";
        if (!string.IsNullOrWhiteSpace(priority) && !PriorityRegex.IsMatch(priority))
            return "Error: Invalid priority. Use emerg, alert, crit, err, warning, notice, info, debug, 0-7 or a range like err..warning.";
        var cmd = BuildJournalctlCommand(serviceName, lines, priority);

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
