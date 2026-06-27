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

    [McpServerTool, Description("List DE-DUPLICATED CVEs across the whole fleet: one entry per CVE-ID with every affected server/container behind it, how long it has been open, and whether a fix exists. Use this instead of the per-target tools to avoid duplicate CVEs.")]
    public static async Task<string> ListCveGroups(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICveFindingsStore store,
        ICveAgeStore ageStore,
        IServerConfigService serverConfig,
        [Description("Min severity to include: Low, Medium, High, Critical (default High)")] string minSeverity = "High",
        [Description("Only CVEs that have an available fix (default false)")] bool fixableOnly = false,
        [Description("Max CVEs to return (default 60)")] int limit = 60)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_cve_groups");
        if (denied != null) return denied;

        var threshold = ParseSeverity(minSeverity);
        var firstSeen = await ageStore.GetFirstSeenAsync();
        var names = serverConfig.GetServers().ToDictionary(s => s.Id, s => s.Name);
        var groups = store.BuildGroups(firstSeen, names)
            .Where(g => g.Severity >= threshold)
            .Where(g => !fixableOnly || g.HasFix)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"De-duplicated CVEs (severity >= {threshold}{(fixableOnly ? ", fixable only" : "")}): {groups.Count} unique");
        if (store.LastScanAt is { } last) sb.AppendLine($"Last scan: {last:yyyy-MM-dd HH:mm} UTC");
        foreach (var g in groups.Take(Math.Clamp(limit, 1, 500)))
        {
            var days = Math.Max(0, (int)g.OpenFor.TotalDays);
            var targets = string.Join(", ", g.Affected
                .Select(a => a.Source == CveSource.Os ? $"{a.ServerName}/OS" : $"{a.ServerName}/{a.TargetLabel}")
                .Distinct().Take(8));
            if (g.Affected.Select(a => a.Source == CveSource.Os ? $"{a.ServerName}/OS" : $"{a.ServerName}/{a.TargetLabel}").Distinct().Count() > 8)
                targets += ", …";
            sb.AppendLine(
                $"- [{g.Severity}] {g.CveId} | open {days}d | {(g.HasFix ? "FIX available" : "no fix")} | " +
                $"{g.InstanceCount} instance(s) on {g.ServerCount} server(s): {targets}");
        }
        if (groups.Count > limit) sb.AppendLine($"… +{groups.Count - limit} more (raise limit)");
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
