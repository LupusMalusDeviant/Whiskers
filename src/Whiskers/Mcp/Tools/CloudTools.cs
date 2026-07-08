using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using Whiskers.Services.Cloud;
using Whiskers.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

/// <summary>
/// Provider-agnostic cloud control. Tools take a Whiskers server (by name or id); the configured
/// per-server provider (Hetzner/Hostinger) and API key are resolved automatically. Works out-of-band
/// — i.e. even when SSH to the server is dead.
/// </summary>
[McpServerToolType]
public class CloudTools
{
    [McpServerTool, Description("List all Whiskers servers that have a cloud provider (Hetzner/Hostinger) configured, with their live power status, type, location, IP, and (Hetzner) traffic usage.")]
    public static async Task<string> ListCloudServers(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICloudControlService cloud)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_cloud_servers");
        if (denied != null) return denied;

        var servers = await cloud.ListAllAsync();
        if (servers.Count == 0) return "Keine Server mit konfiguriertem Cloud-Provider gefunden.";

        var lines = servers.Select(s =>
        {
            var extra = s.Provider == Whiskers.Models.CloudProvider.Hetzner
                ? $" | Traffic: {s.TrafficPercent}% | Backups: {(s.BackupsEnabled ? "an" : "aus")}"
                : "";
            var note = string.IsNullOrEmpty(s.Note) ? "" : $" | {s.Note}";
            return $"- {s.WhiskersName} → {s.Provider} '{s.Name}' (#{s.CloudId}) [{s.Status}] | {s.Type} | {s.Location ?? "-"} | {s.Ipv4 ?? "-"}{extra}{note}";
        });
        return $"Cloud-Server ({servers.Count}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get the live cloud status of a Whiskers server (by name or id): provider, power state, type, location, IP, and (Hetzner) traffic usage and backups.")]
    public static async Task<string> CloudStatus(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "cloud_status");
        if (denied != null) return denied;

        var sw = cloud.ResolveWhiskers(server);
        if (sw == null) return $"Whiskers-Server nicht gefunden: {server}";
        if (sw.CloudProvider == Whiskers.Models.CloudProvider.None || string.IsNullOrWhiteSpace(sw.CloudApiKey))
            return $"Für '{sw.Name}' ist kein Cloud-Provider/API-Key konfiguriert.";

        var info = await cloud.ResolveAsync(sw);
        if (info == null) return $"Kein passender {sw.CloudProvider}-Server zu '{sw.Name}' gefunden (IP-Abgleich).";

        var sb = new StringBuilder();
        sb.AppendLine($"{info.WhiskersName} → {info.Provider} '{info.Name}' (#{info.CloudId})");
        sb.AppendLine($"Status: {info.Status}");
        sb.AppendLine($"Typ: {info.Type ?? "-"}");
        sb.AppendLine($"Standort: {info.Location ?? "-"}");
        sb.AppendLine($"IPv4: {info.Ipv4 ?? "-"}");
        if (info.Provider == Whiskers.Models.CloudProvider.Hetzner)
        {
            sb.AppendLine($"Traffic ausgehend: {info.TrafficPercent}%");
            sb.AppendLine($"Backups: {(info.BackupsEnabled ? "an" : "aus")}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Power on a server (by Whiskers name or id) via its cloud provider.")]
    public static async Task<string> CloudPowerOn(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_power_on", () => cloud.PowerOnAsync(server));

    [McpServerTool, Description("Gracefully shut down a server (by Whiskers name or id) via its cloud provider.")]
    public static async Task<string> CloudShutdown(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_shutdown", () => cloud.ShutdownAsync(server));

    [McpServerTool, Description("Gracefully reboot a server (by Whiskers name or id) via its cloud provider.")]
    public static async Task<string> CloudReboot(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_reboot", () => cloud.RebootAsync(server));

    [McpServerTool, Description("HARD reset (power-cycle) a server via its cloud provider — forceful, use only when a graceful reboot is impossible (e.g. SSH unresponsive). Hetzner: true power-cycle; Hostinger: falls back to a restart (no hard reset available).")]
    public static async Task<string> CloudHardReset(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server)
        => await Guarded(httpContextAccessor, permissionService, "cloud_hard_reset", () => cloud.HardResetAsync(server));

    [McpServerTool, Description("Create a snapshot of a server (by Whiskers name or id) via its cloud provider. Useful before risky changes. Note: Hostinger keeps only ONE snapshot per VM (replaces the previous).")]
    public static async Task<string> CloudCreateSnapshot(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server,
        [Description("Optional snapshot description (Hetzner only)")] string? description = null)
        => await Guarded(httpContextAccessor, permissionService, "cloud_create_snapshot", () => cloud.CreateSnapshotAsync(server, description));

    [McpServerTool, Description("Get recent cloud metrics for a server (by Whiskers name or id). Hetzner type: cpu, disk, network. Hostinger returns raw metric data.")]
    public static async Task<string> CloudMetrics(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService, ICloudControlService cloud,
        [Description("Whiskers server name or id")] string server,
        [Description("Metric type for Hetzner: cpu, disk, or network")] string type = "cpu")
        => await Guarded(httpContextAccessor, permissionService, "cloud_metrics", () => cloud.MetricsAsync(server, type));

    private static async Task<string> Guarded(
        IHttpContextAccessor http, IMcpPermissionService perm, string toolName, Func<Task<string>> action)
    {
        var denied = McpPermissionCheck.CheckAccess(http, perm, toolName);
        if (denied != null) return denied;
        try { return await action(); }
        catch (Exception ex) { return $"Fehler: {ex.Message}"; }
    }
}
