using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Mcp;
using Microsoft.AspNetCore.Http;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class LogTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Search container logs for a pattern (text or regex). Searches every server unless a serverId is given. Returns matching lines across one or all containers.")]
    public static async Task<string> SearchLogs(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILogSearchService searchService,
        [Description("Search pattern (text or regex)")] string pattern,
        [Description("Use regex matching (default: false)")] bool isRegex = false,
        [Description("Container name to search (optional, omit for all)")] string? containerId = null,
        [Description("Server ID (optional, omit to search all servers)")] string? serverId = null,
        [Description("Number of log tail lines to search per container (default: 500)")] int tailLines = 500)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "search_logs");
        if (denied != null) return denied;

        var results = await searchService.SearchAsync(pattern, isRegex, containerId, serverId, tailLines);
        if (!results.Any()) return $"No matches found for '{pattern}'.";

        // Name the server: container names repeat across hosts, so the bare name is ambiguous.
        var lines = results.Select(r => $"[{r.ServerName}/{r.ContainerName}:{r.LineNumber}] {r.Line}");
        return $"Found {results.Count} matches:\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Create a log alert rule that triggers notifications when a pattern is found in container logs. Rules are evaluated on every configured server.")]
    public static async Task<string> CreateLogAlert(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILogMonitorService monitorService,
        [Description("Rule name")] string name,
        [Description("Pattern to match (text or regex)")] string pattern,
        [Description("Use regex matching (default: false)")] bool isRegex = false,
        [Description("Container name (optional, omit for all containers). A name matches that container on EVERY server.")] string? containerName = null,
        [Description("Severity: info, warning, error, critical")] string severity = "error",
        [Description("Cooldown in minutes between alerts")] int cooldownMinutes = 10)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_log_alert");
        if (denied != null) return MutationScope.Fail(denied);

        var rule = await monitorService.CreateRuleAsync(new LogAlertRuleEntity
        {
            Name = name,
            Pattern = pattern,
            IsRegex = isRegex,
            ContainerName = containerName,
            Severity = severity,
            CooldownMinutes = cooldownMinutes
        });

        return $"Log alert rule created:\n  Name: {rule.Name}\n  Pattern: {rule.Pattern}\n  Container: {rule.ContainerName ?? "all"}\n  Severity: {rule.Severity}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List all configured log alert rules.")]
    public static async Task<string> ListLogAlerts(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILogMonitorService monitorService)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_log_alerts");
        if (denied != null) return denied;

        var rules = await monitorService.GetRulesAsync();
        if (!rules.Any()) return "No log alert rules configured.";

        var lines = rules.Select(r =>
            $"- [{(r.Enabled ? "ON" : "OFF")}] {r.Name}: '{r.Pattern}' on {r.ContainerName ?? "all"} ({r.Severity}) — triggered {r.TriggerCount}x");
        return $"Log alert rules ({rules.Count}):\n{string.Join('\n', lines)}";
    }
}
