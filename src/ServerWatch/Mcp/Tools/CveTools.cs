using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Cve;
using ServerWatch.Services.Mcp;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class CveTools
{
    [McpServerTool, Description("Get a CVE summary across all servers: per-server counts of CVE findings (OS + all containers) broken down by severity.")]
    public static string GetCveSummary(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICveFindingsStore store,
        IServerConfigService serverConfig)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_cve_summary");
        if (denied != null) return denied;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CVE summary (latest scan results):");
        if (store.LastScanAt is null)
            sb.AppendLine("  (no scan completed yet — the CVE monitor may be disabled or still warming up)");
        else
            sb.AppendLine($"  Last scan: {store.LastScanAt:yyyy-MM-dd HH:mm:ss} UTC | scanning now: {store.IsScanning}");

        foreach (var s in serverConfig.GetServers())
        {
            var sum = store.SummarizeServer(s.Id);
            if (sum.TotalCount == 0)
            {
                sb.AppendLine($"  - {s.Name} ({s.Id}): no findings");
                continue;
            }
            sb.AppendLine(
                $"  - {s.Name} ({s.Id}): total {sum.TotalCount} " +
                $"(C:{sum.CriticalCount} H:{sum.HighCount} M:{sum.MediumCount} L:{sum.LowCount})");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Get the CVE findings for the host OS on one server (pending security updates and the CVE IDs they address).")]
    public static string GetServerCves(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICveFindingsStore store,
        [Description("Server ID")] string serverId,
        [Description("Min severity to include: Low, Medium, High, Critical (default Low)")] string minSeverity = "Low")
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_server_cves");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId)) return "Server ID is required.";

        var threshold = ParseSeverity(minSeverity);
        var result = store.Get(serverId, null);
        if (result == null) return $"No OS scan recorded yet for server '{serverId}'.";
        if (!string.IsNullOrEmpty(result.Error))
            return $"OS scan error for '{serverId}': {result.Error}";

        var findings = result.Findings
            .Where(f => f.Severity >= threshold)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Package)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"OS CVE findings for server '{serverId}' (scanned {result.ScannedAt:yyyy-MM-dd HH:mm} UTC):");
        sb.AppendLine($"  {findings.Count} finding(s) at severity >= {threshold}");
        foreach (var f in findings.Take(80))
        {
            var fix = string.IsNullOrEmpty(f.FixedVersion) ? "(no fix yet)" : f.FixedVersion;
            sb.AppendLine($"  - [{f.Severity}] {f.CveId} | {f.Package} {f.InstalledVersion ?? "?"} → {fix}");
        }
        if (findings.Count > 80) sb.AppendLine($"  ... +{findings.Count - 80} more");
        return sb.ToString();
    }

    [McpServerTool, Description("Get the CVE findings for a specific container image on a server.")]
    public static string GetContainerCves(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICveFindingsStore store,
        [Description("Container ID")] string containerId,
        [Description("Server ID")] string serverId,
        [Description("Min severity to include: Low, Medium, High, Critical (default Low)")] string minSeverity = "Low")
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_cves");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId)) return "Container ID is required.";
        if (string.IsNullOrWhiteSpace(serverId)) return "Server ID is required.";

        var threshold = ParseSeverity(minSeverity);
        var result = store.Get(serverId, containerId);
        if (result == null) return $"No CVE scan recorded for container '{containerId}' on '{serverId}'.";
        if (!string.IsNullOrEmpty(result.Error))
            return $"Container scan error: {result.Error}";

        var findings = result.Findings
            .Where(f => f.Severity >= threshold)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Package)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"CVE findings for container '{result.ContainerName ?? containerId}' " +
            $"(image {result.Image}, scanned {result.ScannedAt:yyyy-MM-dd HH:mm} UTC):");
        sb.AppendLine($"  {findings.Count} finding(s) at severity >= {threshold}");
        foreach (var f in findings.Take(80))
        {
            var fix = string.IsNullOrEmpty(f.FixedVersion) ? "(no fix yet)" : f.FixedVersion;
            var title = string.IsNullOrEmpty(f.Title) ? "" : $" — {f.Title}";
            sb.AppendLine($"  - [{f.Severity}] {f.CveId} | {f.Package} {f.InstalledVersion ?? "?"} → {fix}{title}");
        }
        if (findings.Count > 80) sb.AppendLine($"  ... +{findings.Count - 80} more");
        return sb.ToString();
    }

    private static CveSeverity ParseSeverity(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "critical" => CveSeverity.Critical,
        "high" => CveSeverity.High,
        "medium" => CveSeverity.Medium,
        "low" => CveSeverity.Low,
        _ => CveSeverity.Low
    };
}
