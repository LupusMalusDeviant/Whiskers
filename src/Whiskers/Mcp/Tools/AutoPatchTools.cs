using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class AutoPatchTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description(
        "Report of the last security auto-patch cycle: which containers were rolled onto the newest image of " +
        "their own tag, which were skipped and why (major version, floating tag without version, compose " +
        "drift, excluded), and which were rolled back. Also shows whether auto-patching is on and when it runs.")]
    public static string GetAutoPatchStatus(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IAutoPatchService autoPatch,
        IOptionsMonitor<AutoPatchSettings> settings)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_auto_patch_status");
        if (denied != null) return denied;
        return Render(settings.CurrentValue, autoPatch.LastCycle, autoPatch.IsRunning);
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description(
        "Start a DRY RUN of the security auto-patch cycle in the background: for every container with fixable " +
        "vulnerabilities it pulls the newest image of the same tag, runs every guard (major version, compose " +
        "drift) and puts the old tag back. Recreates nothing, whatever the settings say. Read the result with " +
        "get_auto_patch_status after a few minutes.")]
    public static string PreviewAutoPatch(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IAutoPatchService autoPatch)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "preview_auto_patch");
        if (denied != null) return MutationScope.Fail(denied);
        if (autoPatch.IsRunning) return MutationScope.Fail("An auto-patch cycle is already running — read get_auto_patch_status.");
        // Even a dry run re-tags and pulls on the host, so every candidate is decided on the caller's authority as it
        // is when the cycle reaches it. Without a person or key behind the call there is nobody to decide for.
        var caller = AutomationOwners.OfCaller(MutationScope.Current?.Principal);
        if (caller is null) return MutationScope.Fail("The preview needs a signed-in user or an API key to act for.");

        // Detached on purpose: a cycle pulls one image per candidate and outlives any tool call.
        _ = Task.Run(() => autoPatch.RunCycleAsync(forceDryRun: true, caller));
        return "Dry run started. Nothing will be recreated. Read get_auto_patch_status in a few minutes.";
    }

    internal static string Render(AutoPatchSettings s, AutoPatchCycleReport? r, bool running)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Auto-patch: {(s.Enabled ? (s.DryRun ? "ON (dry run)" : "ON") : "OFF")}, nightly at " +
                      $"{s.RunAtHourUtc:00}:00 UTC, severity >= {s.MinSeverity}, max {s.MaxPerCycle} per night" +
                      (running ? " — a cycle is running now" : ""));

        if (r is null)
        {
            sb.AppendLine("No cycle has run since Whiskers started.");
            return sb.ToString();
        }

        sb.AppendLine($"Last cycle: {r.StartedAt:u}{(r.DryRun ? " (dry run)" : "")}" +
                      (r.CompletedAt is { } done ? $", finished {done:u}" : ", still running"));
        if (r.Error is not null) sb.AppendLine($"  ** {r.Error} **");

        foreach (var group in r.Entries.GroupBy(e => e.Kind).OrderBy(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine($"  {group.Key} ({group.Count()}):");
            foreach (var e in group)
                sb.AppendLine($"    {e.ServerId}/{e.ContainerName}  {e.Detail}");
        }

        if (r.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Not attempted ({r.Skipped.Count}):");
            foreach (var k in r.Skipped)
                sb.AppendLine($"    {k.ServerId}/{k.ContainerName}  {k.Reason}");
        }
        return sb.ToString();
    }
}
