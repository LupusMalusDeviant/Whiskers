using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Whiskers.Services.ImageUpdate;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class UpdateRiskTools
{
    // R1-48: it pulls the candidate image (a write on the host, image.assess_update), so it needs Write.
    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description(
        "Assess what updating one container's image would change, BEFORE recreating anything. Compares what " +
        "the running image and the candidate image declare — entrypoint, user, exposed ports, expected " +
        "volumes, healthcheck, base OS — and counts which CVEs the update actually closes. Pulls the " +
        "candidate image (starts and restarts nothing). Read-only with respect to running containers.")]
    public static async Task<string> AssessUpdateRisk(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IUpdateRiskService assessor,
        [Description("Server id, e.g. 'local' or 'burgcloud'.")] string serverId,
        [Description("Container name or id prefix.")] string container,
        [Description("Scan the candidate image for CVEs so the benefit has a number. Costs tens of seconds; " +
                     "without it the benefit is reported as unknown, never as zero.")] bool scanCandidate = true)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "assess_update_risk");
        if (denied != null) return MutationScope.Fail(denied);

        var report = await assessor.AssessAsync(serverId, container, scanCandidate);
        // An assessment that could not be made is a failed operation, not an executed one (R1-21).
        return report.Error is null ? Render(report) : MutationScope.Fail(Render(report));
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description(
        "Who else a change to this container touches: which containers get recreated with it, which depend " +
        "on it, which it depends on, and whether the operation would cut the connection Whiskers is using " +
        "to perform it. Reads compose labels only — no pull, no probe. Useful before any restart, update or " +
        "stop, not just updates.")]
    public static async Task<string> GetBlastRadius(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        Whiskers.Services.Docker.IDockerService docker,
        Whiskers.Services.ServerConfig.IServerConfigService serverConfig,
        [Description("Server id, e.g. 'local' or 'infomaniak'.")] string serverId,
        [Description("Container name or id prefix.")] string container,
        [Description("True when the change touches the compose project's configuration — a new or edited " +
                     "override file, a changed environment. Then EVERY service in the project is recreated, " +
                     "not just this one.")] bool changesProjectConfig = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_blast_radius");
        if (denied != null) return denied;

        var all = await docker.ListAllContainersAsync();
        var onServer = all.Where(c => string.Equals(c.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                          .ToList();
        var target = onServer.FirstOrDefault(c =>
            string.Equals(c.Name, container, StringComparison.OrdinalIgnoreCase) ||
            c.Id.StartsWith(container, StringComparison.OrdinalIgnoreCase));

        if (target is null) return $"No container '{container}' on server '{serverId}'.";

        var remote = serverConfig.GetServer(serverId)?.ConnectionType == Whiskers.Models.ConnectionType.TCP;
        return RenderRadius(BlastRadiusAssessor.Assess(target, onServer, remote, changesProjectConfig));
    }

    internal static string RenderRadius(BlastRadius r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Blast radius for {r.Target}");

        if (r.SeversOwnPath)
        {
            sb.AppendLine();
            sb.AppendLine("  ** THIS CUTS THE CONNECTION IT TRAVELS THROUGH **");
            sb.AppendLine("  Whiskers reaches this server through a proxy container in the same project. Run " +
                          "the operation detached (systemd-run / nohup), or it dies mid-flip and the server " +
                          "is out of reach from here.");
        }

        sb.AppendLine();
        if (r.Affected.Count == 0)
            sb.AppendLine("  No other container in this project is affected.");
        else
        {
            sb.AppendLine($"  {r.Affected.Count} other container(s) affected:");
            foreach (var a in r.Affected)
                sb.AppendLine($"    [{a.Kind}] {a.Name} — {a.WhatHappens}");
        }

        sb.AppendLine();
        sb.AppendLine("  NOT visible from here:");
        foreach (var b in r.BlindSpots)
            sb.AppendLine($"    - {b}");

        return sb.ToString();
    }

    internal static string Render(UpdateRiskReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Update risk for {r.ContainerName} on {r.ServerId} ({r.ImageRef})");

        if (r.Error is not null)
        {
            // An assessment that could not be made must say so. "No findings" here would read as "safe".
            sb.AppendLine($"  ASSESSMENT FAILED: {r.Error}");
            sb.AppendLine("  This is not a verdict. Nothing was checked.");
            return sb.ToString();
        }

        if (!r.UpdateAvailable)
        {
            sb.AppendLine("  Already on the newest image for this tag — nothing to decide.");
            return sb.ToString();
        }

        var risk = r.Risk!;
        sb.AppendLine($"  Risk: {risk.Level.ToString().ToUpperInvariant()}   " +
                      (risk.CvesClosed is { } n
                          ? $"Closes {n} vulnerabilit{(n == 1 ? "y" : "ies")}."
                          : "Benefit unknown — the candidate image was not scanned."));
        sb.AppendLine($"  {r.CurrentDigest?[..Math.Min(19, r.CurrentDigest.Length)]} → " +
                      $"{r.CandidateDigest?[..Math.Min(19, r.CandidateDigest?.Length ?? 0)]}");

        if (risk.Findings.Count == 0)
            sb.AppendLine("  Nothing detectable changed in what the image declares.");
        else
        {
            sb.AppendLine();
            sb.AppendLine("  What changes:");
            foreach (var f in risk.Findings.OrderByDescending(f => f.Level))
                sb.AppendLine($"    [{f.Level.ToString().ToUpperInvariant()}] {f.What} — {f.WhyItMatters}");
        }

        // Always. A verdict that hid its own limits would be worse than no verdict — this compares
        // declarations, not behaviour.
        sb.AppendLine();
        sb.AppendLine("  NOT covered by this assessment:");
        foreach (var b in risk.BlindSpots)
            sb.AppendLine($"    - {b}");
        sb.AppendLine();
        sb.AppendLine("  \"Low risk\" here means nothing detectable changed. It does not mean safe.");

        return sb.ToString();
    }
}
