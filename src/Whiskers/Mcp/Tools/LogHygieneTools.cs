using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Whiskers.Models;
using Whiskers.Services.LogMonitor.Hygiene;
using Whiskers.Services.Mcp;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Mcp.Tools;

/// <summary>Log hygiene, read-only (Plan-0007 WP-MCP).
///
/// <para>Answers the question an agent cannot otherwise answer: "why is this container producing no log
/// alerts?" Without it, a container excluded from the scan is indistinguishable from a container with nothing
/// to report — and the agent would go looking for a fault in the rules.</para>
///
/// <para>No write counterpart, deliberately. Changing what is scanned means deciding to stop watching
/// something, and the remediation for an oversized log recreates the container. Both belong to a person; the
/// report hands over the finding and, later, the exact command — it does not run it.</para></summary>
[McpServerToolType]
public class LogHygieneTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Report the log-file situation on the fleet: which container logs are growing without a rotation limit (with size, growth per day, share of the free disk and the exact command to fix it), and which containers the log-alert scan steps over and why. Excluded containers are still covered by health, metric and CVE monitoring; only their log content is ignored. Read-only: this changes nothing and runs nothing — setting a rotation limit recreates the container, which is a person's decision.")]
    public static string GetLogHygieneReport(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILogScanExclusions exclusions,
        ILogInventory inventory,
        IServerConfigService servers,
        [Description("Limit the report to one server (optional)")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_log_hygiene_report");
        if (denied != null) return denied;

        return string.Join("\n\n", InventorySection(inventory, servers, serverId), ExclusionSection(exclusions, servers, serverId));
    }

    /// <summary>Findings first: an unbounded log is the thing that fills a disk, and the exclusions are only
    /// context for why some containers are quiet.</summary>
    private static string InventorySection(ILogInventory inventory, IServerConfigService servers, string? serverId)
    {
        var entries = inventory.Current()
            .Where(e => string.IsNullOrWhiteSpace(serverId) || string.Equals(e.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
            return "Log inventory: no readings yet. The survey runs daily; on a fresh start the first one is " +
                   "still pending, and growth rates need a second reading a day later.";

        var findings = entries
            .Where(e => LogHygieneAdvice.Severity(e) != LogHygieneSeverity.None)
            .OrderByDescending(e => e.SizeBytes ?? 0)
            .ToList();

        var unknown = entries.Count(e => e.UnknownReason is not null);
        var header = $"Log inventory: {entries.Count} container(s) surveyed, {findings.Count} without a rotation limit" +
                     (unknown > 0 ? $", {unknown} whose size could not be read (reported as unknown rather than guessed)" : "") + ".";

        if (findings.Count == 0) return header + " Nothing is growing without a limit.";

        var lines = findings.Select(e =>
        {
            var name = servers.GetServer(e.ServerId)?.Name ?? e.ServerId;
            var level = LogHygieneAdvice.Severity(e) == LogHygieneSeverity.Alert ? "ALERT" : "note";
            return $"- [{level}] {LogHygieneAdvice.Describe(e, name)}";
        });

        return $"{header}\n{string.Join('\n', lines)}\n\n{LogHygieneAdvice.TriggerNotCause}";
    }

    private static string ExclusionSection(ILogScanExclusions exclusions, IServerConfigService servers, string? serverId)
    {
        var all = exclusions.Current();
        var scoped = string.IsNullOrWhiteSpace(serverId)
            ? all
            : all.Where(e => string.Equals(e.ServerId, serverId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (scoped.Count == 0)
        {
            // "Nothing excluded" is a real answer and a slightly suspicious one on a host reached through a
            // proxy — say so rather than leaving the agent to read silence as confirmation.
            return string.IsNullOrWhiteSpace(serverId)
                ? "No containers are excluded from the log-alert scan. On a host Whiskers reaches through a " +
                  "tunnel or socket proxy, expect at least one — if there is none, the access path may not be " +
                  "detectable from the container list and may need WHISKERS_SELF_CONTAINERS."
                : $"No containers are excluded from the log-alert scan on '{serverId}'.";
        }

        var lines = scoped
            .GroupBy(e => e.ServerId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var name = servers.GetServer(group.Key)?.Name ?? group.Key;
                var entries = group.Select(e => $"  - {e.ContainerName} [{e.Reason}] — {e.Detail}");
                return $"{name} ({group.Key}):\n{string.Join('\n', entries)}";
            });

        return $"Containers excluded from the log-alert scan ({scoped.Count}):\n{string.Join("\n\n", lines)}\n\n" +
               "These containers are still monitored for health, metrics and CVEs — only their log content is " +
               "skipped. Excluding the access path removes the trigger of the 2026-08-26 incident, not its " +
               "cause: the cause was a log fetch that was abandoned rather than cancelled.";
    }
}
