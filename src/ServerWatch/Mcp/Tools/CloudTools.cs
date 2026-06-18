using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using ServerWatch.Services.Cloud;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

/// <summary>
/// Provider-agnostic cloud control. Tools take a ServerWatch server (by name or id); the configured
/// per-server provider (Hetzner/Hostinger) and API key are resolved automatically. Works out-of-band
/// — i.e. even when SSH to the server is dead.
/// </summary>
[McpServerToolType]
public class CloudTools
{
    [McpServerTool, Description("List all ServerWatch servers that have a cloud provider (Hetzner/Hostinger) configured, with their live power status, type, location, IP, and (Hetzner) traffic usage.")]
    public static async Task<string> ListCloudServers(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        CloudControlService cloud)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_cloud_servers");
        if (denied != null) return denied;

        var servers = await cloud.ListAllAsync();
        if (servers.Count == 0) return "Keine Server mit konfiguriertem Cloud-Provider gefunden.";

        var lines = servers.Select(s =>
        {
            var extra = s.Provider == ServerWatch.Models.CloudProvider.Hetzner
                ? $" | Traffic: {s.TrafficPercent}% | Backups: {(s.BackupsEnabled ? "an" : "aus")}"
                : "";
            var note = string.IsNullOrEmpty(s.Note) ? "" : $" | {s.Note}";
            return $"- {s.ServerWatchName} → {s.Provider} '{s.Name}' (#{s.CloudId}) [{s.Status}] | {s.Type} | {s.Location ?? "-"} | {s.Ipv4 ?? "-"}{extra}{note}";
        });
        return $"Cloud-Server ({servers.Count}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get the live cloud status of a ServerWatch server (by name or id): provider, power state, type, location, IP, and (Hetzner) traffic usage and backups.")]
    public static async Task<string> CloudStatus(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "cloud_status");
        if (denied != null) return denied;

        var sw = cloud.ResolveServerWatch(server);
        if (sw == null) return $"ServerWatch-Server nicht gefunden: {server}";
        if (sw.CloudProvider == ServerWatch.Models.CloudProvider.None || string.IsNullOrWhiteSpace(sw.CloudApiKey))
            return $"Für '{sw.Name}' ist kein Cloud-Provider/API-Key konfiguriert.";

        var info = await cloud.ResolveAsync(sw);
        if (info == null) return $"Kein passender {sw.CloudProvider}-Server zu '{sw.Name}' gefunden (IP-Abgleich).";

        var sb = new StringBuilder();
        sb.AppendLine($"{info.ServerWatchName} → {info.Provider} '{info.Name}' (#{info.CloudId})");
        sb.AppendLine($"Status: {info.Status}");
        sb.AppendLine($"Typ: {info.Type ?? "-"}");
        sb.AppendLine($"Standort: {info.Location ?? "-"}");
        sb.AppendLine($"IPv4: {info.Ipv4 ?? "-"}");
        if (info.Provider == ServerWatch.Models.CloudProvider.Hetzner)
        {
            sb.AppendLine($"Traffic ausgehend: {info.TrafficPercent}%");
            sb.AppendLine($"Backups: {(info.BackupsEnabled ? "an" : "aus")}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Power on a server (by ServerWatch name or id) via its cloud provider.")]
    public static async Task<string> CloudPowerOn(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_power_on", () => cloud.PowerOnAsync(server));

    [McpServerTool, Description("Gracefully shut down a server (by ServerWatch name or id) via its cloud provider.")]
    public static async Task<string> CloudShutdown(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_shutdown", () => cloud.ShutdownAsync(server));

    [McpServerTool, Description("Gracefully reboot a server (by ServerWatch name or id) via its cloud provider.")]
    public static async Task<string> CloudReboot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_reboot", () => cloud.RebootAsync(server));

    [McpServerTool, Description("HARD reset (power-cycle) a server via its cloud provider — forceful, use only when a graceful reboot is impossible (e.g. SSH unresponsive). Hetzner: true power-cycle; Hostinger: falls back to a restart (no hard reset available).")]
    public static async Task<string> CloudHardReset(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_hard_reset", () => cloud.HardResetAsync(server));

    [McpServerTool, Description("Create a snapshot of a server (by ServerWatch name or id) via its cloud provider. Useful before risky changes. Note: Hostinger keeps only ONE snapshot per VM (replaces the previous).")]
    public static async Task<string> CloudCreateSnapshot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server,
        [Description("Optional snapshot description (Hetzner only)")] string? description = null)
        => await Guarded(httpContextAccessor, permissionService, "cloud_create_snapshot", () => cloud.CreateSnapshotAsync(server, description));

    [McpServerTool, Description("Get recent cloud metrics for a server (by ServerWatch name or id). Hetzner type: cpu, disk, network. Hostinger returns raw metric data.")]
    public static async Task<string> CloudMetrics(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, CloudControlService cloud,
        [Description("ServerWatch server name or id")] string server,
        [Description("Metric type for Hetzner: cpu, disk, or network")] string type = "cpu")
        => await Guarded(httpContextAccessor, permissionService, "cloud_metrics", () => cloud.MetricsAsync(server, type));

    private static async Task<string> Guarded(
        IHttpContextAccessor http, McpPermissionService perm, string toolName, Func<Task<string>> action)
    {
        var denied = McpPermissionCheck.CheckAccess(http, perm, toolName);
        if (denied != null) return denied;
        try { return await action(); }
        catch (Exception ex) { return $"Fehler: {ex.Message}"; }
    }
}
