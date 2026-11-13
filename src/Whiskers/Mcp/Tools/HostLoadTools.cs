using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Metrics;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Mcp.Tools;

/// <summary>Host load, judged rather than merely reported (Plan-0004 WP-MCP).
///
/// <para>The agent could already read container stats. What it could not do is ask the question that took six
/// days to answer by hand on 2026-08-26: <em>is this host busy with something that is not a container?</em>
/// Container stats alone cannot answer it — <c>dockerd</c> appears in none of them.</para>
///
/// <para>Read-only, and deliberately without a threshold-setting counterpart: raising a threshold is how an
/// inconvenient alert gets silenced, and that decision belongs to a person who will remember making it.</para></summary>
[McpServerToolType]
public class HostLoadTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Report each server's current host load and how much of it the containers actually account for. Use this when a host looks busy but no container explains it — that gap means a process outside the containers (dockerd, a backup job, a runaway service) is the likely cause, which per-container stats can never show. The two CPU figures use different conventions and are reconciled here. Read-only.")]
    public static async Task<string> GetHostLoad(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IMetricsSource metricsSource,
        IServerConfigService servers,
        [Description("Limit to one server (optional)")] string? serverId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_host_load");
        if (denied != null) return denied;

        var infos = await metricsSource.GetAllServerSystemInfoAsync(cancellationToken);
        var lines = new List<string>();

        foreach (var (id, info) in infos)
        {
            if (!string.IsNullOrWhiteSpace(serverId) && !string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!info.IsReachable)
            {
                // Named rather than skipped: a server missing from this list would read as "nothing to
                // report", which is the confusion the whole self-protection work is about.
                lines.Add($"- {info.ServerName} ({id}): unreachable — no reading, which is not the same as no load.");
                continue;
            }

            var memPercent = info.MemoryTotalBytes > 0
                ? info.MemoryUsedBytes * 100.0 / info.MemoryTotalBytes
                : 0;

            lines.Add(
                $"- {info.ServerName} ({id}): {info.CpuUsagePercent:F0}% CPU of the whole machine " +
                $"({info.CpuCount} core(s)), {memPercent:F0}% memory.");
        }

        if (lines.Count == 0)
            return string.IsNullOrWhiteSpace(serverId)
                ? "No servers answered."
                : $"Server '{serverId}' not found or did not answer.";

        return "Host load:\n" + string.Join('\n', lines) +
               "\n\nHost CPU is a percentage of the whole machine; container CPU readings use Docker's scale, " +
               "where one fully busy core is 100 — so a 2-core host can show containers summing to 200. " +
               "Divide the container sum by the core count before comparing it with the host figure. A large " +
               "unexplained gap means a process outside the containers is doing the work; short-lived " +
               "containers are missing from the sum, so treat it as a strong hint rather than proof.";
    }
}
