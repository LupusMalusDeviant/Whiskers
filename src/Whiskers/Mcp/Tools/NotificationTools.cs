using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Notifications;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

/// <summary>Read-only view of the alert history (Plan-0013 WP4).
///
/// <para>This is the "what has been going on?" tool. Until now the agent could read container state and logs
/// but not the alerts Whiskers itself had already raised — so it re-derived from raw logs what the system had
/// long since concluded, or missed it entirely.</para>
///
/// <para>Deliberately no sending tool: an agent that can dispatch notifications can also generate noise on the
/// operator's channels, and a notification is the one signal that has to stay trustworthy.</para></summary>
[McpServerToolType]
public class NotificationTools
{
    private const int MaxTake = 200;

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the alerts and events Whiskers has raised recently (container down, restart loops, log-alert hits, CVE findings, agent actions), newest first. Optionally filter by severity or event type. Read-only — this cannot send notifications.")]
    public static async Task<string> ListRecentAlerts(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IInAppNotificationStore notifications,
        [Description("Severity filter: info, warning, error, critical (optional)")] string? severity = null,
        [Description("Event type filter, e.g. container_down, log_alert (optional)")] string? eventType = null,
        [Description("How many to return, newest first (default 25, max 200)")] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_recent_alerts");
        if (denied != null) return denied;

        // Clamp rather than reject: an agent asking for 10000 should get a usable answer, not an error, and the
        // cap keeps one call from returning the entire retained history.
        var take = Math.Clamp(limit, 1, MaxTake);

        var total = await notifications.CountAsync(severity, eventType, cancellationToken);
        var entries = await notifications.QueryAsync(severity, eventType, skip: 0, take: take, ct: cancellationToken);
        if (entries.Count == 0)
            return "No alerts match" + Describe(severity, eventType) + ".";

        var lines = entries.Select(n =>
            $"- [{n.Timestamp:yyyy-MM-dd HH:mm} UTC] {n.Severity.ToUpperInvariant()} {n.EventType}: {n.Title}" +
            (string.IsNullOrWhiteSpace(n.Detail) ? "" : $" — {n.Detail}"));

        var shown = entries.Count < total ? $"{entries.Count} of {total}" : $"{total}";
        return $"Alerts ({shown}){Describe(severity, eventType)}:\n{string.Join('\n', lines)}";
    }

    private static string Describe(string? severity, string? eventType)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(severity)) filters.Add($"severity={severity}");
        if (!string.IsNullOrWhiteSpace(eventType)) filters.Add($"type={eventType}");
        return filters.Count == 0 ? "" : $" [{string.Join(", ", filters)}]";
    }
}
