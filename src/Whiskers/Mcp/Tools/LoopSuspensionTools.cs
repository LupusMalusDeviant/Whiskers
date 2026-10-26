using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Observability;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

/// <summary>The emergency stop, exposed to the agent (Plan-0005 WP2).
///
/// <para>On 2026-08-26 the thing generating the load was Whiskers itself, and the only way to stop it was SSH
/// on the affected host — past the tool causing the problem. An agent that can diagnose that situation but
/// cannot act on it is an agent that writes a good post-mortem while the host is still on fire.</para>
///
/// <para><b>Deliberately asymmetric.</b> Pausing is bounded: an agent may stop background checks for at most
/// <see cref="MaxAgentPauseMinutes"/> minutes and must say why. There is no "until revoked" over MCP — an
/// open-ended pause is a decision about how much blindness the operator accepts, and that is a person's call.
/// Resuming has no such limit, because turning monitoring back ON is never the dangerous direction.</para></summary>
[McpServerToolType]
public class LoopSuspensionTools
{
    /// <summary>The longest pause an agent may set. Past this, a person decides.</summary>
    public const int MaxAgentPauseMinutes = 120;

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List servers whose background checks are currently paused, with the reason, who paused them (an operator or Whiskers itself), and when the pause expires. A paused server produces no health, log, metric or CVE findings — silence from it means nothing is being looked at, not that nothing is wrong.")]
    public static string ListPausedServers(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILoopSuspensionService suspension,
        IServerConfigService servers)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_paused_servers");
        if (denied != null) return denied;

        var paused = suspension.Current();
        if (paused.Count == 0) return "No servers are paused — background checks are running everywhere.";

        var lines = paused.Select(p =>
        {
            var name = servers.GetServer(p.ServerId)?.Name ?? p.ServerId;
            var who = p.Automatic ? "Whiskers paused itself" : "paused by an operator";
            var remaining = p.Until - DateTime.UtcNow;
            var until = remaining > TimeSpan.FromDays(365)
                ? "until revoked"
                : $"for another {remaining.TotalMinutes:0} min";
            return $"- {name} ({p.ServerId}): {who} — {p.Reason}; {until}; paused since {p.Since:yyyy-MM-dd HH:mm} UTC";
        });

        return $"Paused servers ({paused.Count}):\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Pause Whiskers' own background checks (health, logs, metrics, CVE, image updates) for one server, for a bounded number of minutes. Use this when Whiskers itself is the load on a host. Interactive access keeps working, so the server can still be inspected. The pause is announced, expires by itself, and is reminded about if it outlives its reason. This does NOT stop the containers on that server and does NOT block anything running there.")]
    public static string PauseServerChecks(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILoopSuspensionService suspension,
        IServerConfigService servers,
        [Description("Server ID whose background checks should pause")] string serverId,
        [Description("Why — this is shown in the alert and in the reminder; be specific")] string reason,
        [Description($"Minutes to pause, 1 to 120 (default 30). Longer pauses are an operator decision, not an agent's.")] int minutes = 30)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "pause_server_checks");
        if (denied != null) return MutationScope.Fail(denied);

        if (string.IsNullOrWhiteSpace(serverId)) return MutationScope.Fail("Error: serverId is required.");
        if (string.IsNullOrWhiteSpace(reason))
            return MutationScope.Fail("Error: a reason is required. A pause with no stated cause is indistinguishable from a bug.");

        var server = servers.GetServer(serverId);
        if (server == null) return MutationScope.Fail($"Error: server '{serverId}' not found.");

        // Clamped, not rejected: an agent asking for a day gets two hours and is told so, rather than getting
        // an error while the host it is trying to protect stays under load.
        var bounded = Math.Clamp(minutes, 1, MaxAgentPauseMinutes);
        var until = DateTime.UtcNow.AddMinutes(bounded);
        suspension.Suspend(server.Id, until, reason, automatic: false);

        var note = bounded != minutes
            ? $" (asked for {minutes}, capped at {MaxAgentPauseMinutes} — anything longer is an operator decision)"
            : "";
        return $"Background checks for {server.Name} are paused until {until:HH:mm} UTC{note}. " +
               "Nothing there is being checked meanwhile; the pause was announced and expires on its own.";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Resume Whiskers' background checks for a server that was paused. Safe to call even if it is not paused. Turning monitoring back on is never the dangerous direction, so this has no time limit.")]
    public static string ResumeServerChecks(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ILoopSuspensionService suspension,
        IServerConfigService servers,
        [Description("Server ID whose background checks should resume")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "resume_server_checks");
        if (denied != null) return MutationScope.Fail(denied);

        if (string.IsNullOrWhiteSpace(serverId)) return MutationScope.Fail("Error: serverId is required.");

        var server = servers.GetServer(serverId);
        if (server == null) return MutationScope.Fail($"Error: server '{serverId}' not found.");

        var wasPaused = suspension.IsSuspended(server.Id);
        suspension.Resume(server.Id);

        return wasPaused
            ? $"Background checks for {server.Name} are running again."
            : $"{server.Name} was not paused — background checks were already running.";
    }
}
