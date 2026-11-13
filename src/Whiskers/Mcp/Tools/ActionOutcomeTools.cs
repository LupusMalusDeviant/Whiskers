using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Observability.Outcomes;

namespace Whiskers.Mcp.Tools;

/// <summary>Whether Whiskers' automatic actions actually achieve anything (Plan-0006 WP-MCP).
///
/// <para>The agent can already see what was done. What it could not see is whether any of it helped — and an
/// agent that cannot tell will repeat an ineffective action confidently, which is the failure mode this whole
/// package exists to prevent.</para>
///
/// <para>Read-only, with no counterpart that clears or re-runs anything: an agent able to erase its own track
/// record could hide exactly the pattern this is meant to expose.</para></summary>
[McpServerToolType]
public class ActionOutcomeTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Report whether Whiskers' automatic actions actually worked: per action kind, how many were effective, how many changed nothing, and how many could not be measured at all. Read this before repeating an action — a kind with a poor hit rate is one where the cause probably lies elsewhere. A high 'not measurable' count means the checking itself has broken down, which is NOT the same as things being fine. Read-only.")]
    public static async Task<string> GetActionOutcomes(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IActionOutcomeService outcomes,
        [Description("How many days back to summarise (default 7, max 90)")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_action_outcomes");
        if (denied != null) return denied;

        var window = TimeSpan.FromDays(Math.Clamp(days, 1, 90));
        var tallies = await outcomes.TalliesAsync(DateTime.UtcNow - window, cancellationToken);
        var overdue = await outcomes.OverdueCountAsync(DateTime.UtcNow, cancellationToken);

        if (tallies.Count == 0)
            return $"Whiskers has taken no automatic actions in the last {window.TotalDays:0} days.";

        var lines = tallies.Select(t =>
        {
            var rate = t.HitRate is { } r ? $"{r * 100:0}% effective" : "nothing judged yet";
            var unmeasured = t.NotMeasurable > 0 ? $", {t.NotMeasurable} not measurable" : "";
            var pending = t.Pending > 0 ? $", {t.Pending} still within their check window" : "";
            return $"- {t.ActionKind}: {rate} ({t.Worked} worked, {t.DidNotWork} changed nothing{unmeasured}{pending})";
        });

        var report = $"Automatic actions over the last {window.TotalDays:0} days:\n{string.Join('\n', lines)}";

        var unmeasurable = tallies.Sum(t => t.NotMeasurable);
        var judged = tallies.Sum(t => t.Judged);

        if (unmeasurable > 0 && unmeasurable >= judged)
            report += "\n\nMore outcomes could NOT be measured than could. Treat every hit rate above as " +
                      "unreliable — this is the checking failing, not the fleet being healthy.";

        if (overdue > 0)
            report += $"\n\n{overdue} check window(s) came due and were never judged. If that number keeps " +
                      "climbing, the outcome sweep has stopped running.";

        report += "\n\nAn action that changed nothing usually means the cause was somewhere other than where " +
                  "the action was aimed. For Whiskers' own throttles it means something sharper: monitoring " +
                  "was taken away from a server for no benefit.";

        return report;
    }
}
