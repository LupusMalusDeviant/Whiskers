using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using ServerWatch.Models.Hetzner;
using ServerWatch.Services.Hetzner;
using ServerWatch.Services.Mcp;
using ServerWatch.Services.ServerConfig;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class HetznerTools
{
    private const string Disabled = "Hetzner-Integration ist nicht aktiviert. Bitte unter Einstellungen ein API-Token hinterlegen.";

    // ─────────────────────────── Read ───────────────────────────

    [McpServerTool, Description("List all Hetzner Cloud servers with status, type, location, public IP, outgoing-traffic usage, backups, and the linked ServerWatch server (matched by IP).")]
    public static async Task<string> ListHetznerServers(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config,
        ServerConfigService serverConfig)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_hetzner_servers");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var servers = await hetzner.ListServersAsync();
        if (servers.Count == 0) return "Keine Hetzner-Server gefunden.";

        var lines = servers.Select(s =>
        {
            var linked = LinkedServerName(serverConfig, s);
            return $"- {s.Name} (#{s.Id}) [{s.Status}] | {s.ServerType?.Name} | {s.Datacenter?.Location?.City ?? s.Datacenter?.Location?.Name} " +
                   $"| IP: {s.Ipv4 ?? "-"} | Traffic: {s.TrafficUsedPercent}% | Backups: {(s.BackupsEnabled ? "an" : "aus")}" +
                   (linked != null ? $" | ServerWatch: {linked}" : "");
        });
        return $"Gefunden: {servers.Count} Hetzner-Server:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get detailed information about a Hetzner Cloud server (by numeric ID or name): status, type, datacenter, traffic usage, backups, rescue mode, and linked ServerWatch server.")]
    public static async Task<string> GetHetznerServer(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config,
        ServerConfigService serverConfig,
        [Description("Hetzner server numeric ID or name")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_hetzner_server");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";

        var sb = new StringBuilder();
        sb.AppendLine($"Name: {s.Name} (#{s.Id})");
        sb.AppendLine($"Status: {s.Status}");
        sb.AppendLine($"Typ: {s.ServerType?.Name} ({s.ServerType?.Cores} Kerne, {s.ServerType?.Memory} GB RAM, {s.ServerType?.Disk} GB Disk)");
        sb.AppendLine($"Standort: {s.Datacenter?.Name} ({s.Datacenter?.Location?.City}, {s.Datacenter?.Location?.Country})");
        sb.AppendLine($"IPv4: {s.Ipv4 ?? "-"}");
        sb.AppendLine($"IPv6: {s.PublicNet?.Ipv6?.Ip ?? "-"}");
        sb.AppendLine($"Image: {s.Image?.Name ?? s.Image?.Description ?? "-"}");
        sb.AppendLine($"Traffic ausgehend: {Gb(s.OutgoingTraffic)} / {Gb(s.IncludedTraffic)} inklusive ({s.TrafficUsedPercent}%)");
        sb.AppendLine($"Backups: {(s.BackupsEnabled ? $"an (Fenster {s.BackupWindow})" : "aus")}");
        sb.AppendLine($"Rescue-Mode: {(s.RescueEnabled ? "AKTIV" : "aus")}");
        var linked = LinkedServerName(serverConfig, s);
        sb.AppendLine($"ServerWatch-Server: {linked ?? "(nicht verknüpft)"}");
        return sb.ToString();
    }

    [McpServerTool, Description("Get recent Hetzner Cloud metrics for a server (by ID or name). Type is one of: cpu, disk, network. Returns the latest sampled values.")]
    public static async Task<string> GetHetznerServerMetrics(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server,
        [Description("Metric type: cpu, disk, or network")] string type = "cpu")
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_hetzner_server_metrics");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var valid = new[] { "cpu", "disk", "network" };
        type = type.ToLowerInvariant();
        if (!valid.Contains(type)) return $"Ungültiger Typ '{type}'. Erlaubt: cpu, disk, network.";

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";

        var end = DateTime.UtcNow;
        var metrics = await hetzner.GetMetricsAsync(s.Id, type, end.AddMinutes(-30), end, 60);
        if (metrics == null || metrics.TimeSeries.Count == 0)
            return $"Keine {type}-Metriken für {s.Name} verfügbar.";

        var sb = new StringBuilder();
        sb.AppendLine($"{type}-Metriken für {s.Name} (#{s.Id}), letzte Werte:");
        foreach (var (key, series) in metrics.TimeSeries)
        {
            var latest = series.Latest;
            sb.AppendLine($"- {key}: {(latest.HasValue ? latest.Value.ToString("0.###") : "-")}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("List Hetzner Cloud snapshots (manual images) with size and source server.")]
    public static async Task<string> ListHetznerSnapshots(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_hetzner_snapshots");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var images = await hetzner.ListSnapshotsAsync();
        if (images.Count == 0) return "Keine Snapshots gefunden.";

        var lines = images.Select(i =>
            $"- #{i.Id} {i.Description ?? "(ohne Beschreibung)"} [{i.Status}] | {(i.ImageSize.HasValue ? $"{i.ImageSize:F1} GB" : "-")} | von: {i.CreatedFrom?.Name ?? "-"} | {i.Created}");
        return $"Gefunden: {images.Count} Snapshot(s):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("List available Hetzner Cloud server types (for resizing) with cores, memory, and disk.")]
    public static async Task<string> ListHetznerServerTypes(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_hetzner_server_types");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var types = await hetzner.ListServerTypesAsync();
        if (types.Count == 0) return "Keine Server-Typen gefunden.";

        var lines = types.OrderBy(t => t.Cores).ThenBy(t => t.Memory)
            .Select(t => $"- {t.Name}: {t.Cores} Kerne, {t.Memory} GB RAM, {t.Disk} GB Disk");
        return $"Verfügbare Server-Typen ({types.Count}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("List Hetzner Cloud firewalls and their rules.")]
    public static async Task<string> ListHetznerFirewalls(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_hetzner_firewalls");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var firewalls = await hetzner.ListFirewallsAsync();
        if (firewalls.Count == 0) return "Keine Hetzner-Firewalls gefunden.";

        var sb = new StringBuilder();
        foreach (var fw in firewalls)
        {
            sb.AppendLine($"Firewall '{fw.Name}' (#{fw.Id}), {fw.Rules.Count} Regel(n), angewendet auf {fw.AppliedTo?.Count ?? 0} Ressource(n):");
            foreach (var r in fw.Rules)
            {
                var ips = r.Direction == "in" ? string.Join(",", r.SourceIps ?? new()) : string.Join(",", r.DestinationIps ?? new());
                sb.AppendLine($"  [{r.Direction}] {r.Protocol}/{r.Port ?? "-"} {ips}");
            }
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Get Hetzner Cloud pricing summary (currency, VAT, traffic overage price, backup surcharge).")]
    public static async Task<string> GetHetznerPricing(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHetznerService hetzner,
        HetznerConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_hetzner_pricing");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var p = await hetzner.GetPricingAsync();
        if (p == null) return "Keine Preisinformationen verfügbar.";

        var sb = new StringBuilder();
        sb.AppendLine($"Währung: {p.Currency ?? "-"}");
        sb.AppendLine($"MwSt.-Satz: {p.VatRate ?? "-"}%");
        sb.AppendLine($"Traffic-Überschreitung: {p.Traffic ?? "-"} pro TB");
        sb.AppendLine($"Backup-Aufschlag: {p.ServerBackup?.Percentage ?? "-"}% des Serverpreises");
        return sb.ToString();
    }

    // ─────────────────────────── Power / lifecycle (write) ───────────────────────────

    [McpServerTool, Description("Power on a Hetzner Cloud server (by ID or name).")]
    public static async Task<string> HetznerPowerOn(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_power_on", server,
            (h, id) => h.PowerOnAsync(id), "wird eingeschaltet");

    [McpServerTool, Description("Gracefully shut down a Hetzner Cloud server via ACPI (by ID or name). The OS performs a clean shutdown.")]
    public static async Task<string> HetznerShutdown(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_shutdown", server,
            (h, id) => h.ShutdownAsync(id), "wird heruntergefahren (ACPI)");

    [McpServerTool, Description("Gracefully reboot a Hetzner Cloud server (by ID or name).")]
    public static async Task<string> HetznerReboot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_reboot", server,
            (h, id) => h.RebootAsync(id), "wird neu gestartet");

    [McpServerTool, Description("HARD reset (power-cycle) a Hetzner Cloud server (by ID or name). Forceful — like pulling the plug; use only when a graceful reboot/shutdown is not possible (e.g. SSH is unresponsive).")]
    public static async Task<string> HetznerReset(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_reset", server,
            (h, id) => h.ResetAsync(id), "wird hart zurückgesetzt (power-cycle)");

    [McpServerTool, Description("Enable rescue mode on a Hetzner Cloud server, then reset it to boot into rescue. Returns the temporary root password. Use for recovery when the OS won't boot.")]
    public static async Task<string> HetznerEnableRescue(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_enable_rescue");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";

        var resp = await hetzner.EnableRescueAsync(s.Id);
        var pw = resp?.RootPassword;
        return $"Rescue-Mode für {s.Name} aktiviert. Jetzt einen Reset/Reboot ausführen, um hineinzubooten.\n" +
               $"Temporäres root-Passwort: {pw ?? "(nicht zurückgegeben)"}";
    }

    [McpServerTool, Description("Disable rescue mode on a Hetzner Cloud server (by ID or name).")]
    public static async Task<string> HetznerDisableRescue(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_disable_rescue", server,
            (h, id) => h.DisableRescueAsync(id), "Rescue-Mode deaktiviert");

    // ─────────────────────────── Snapshots / backups (write) ───────────────────────────

    [McpServerTool, Description("Create a snapshot (image) of a Hetzner Cloud server (by ID or name). Useful before risky changes. Snapshots incur storage cost until deleted.")]
    public static async Task<string> HetznerCreateSnapshot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server,
        [Description("Optional snapshot description")] string? description = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_create_snapshot");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";

        var resp = await hetzner.CreateSnapshotAsync(s.Id, description);
        return $"Snapshot von {s.Name} wird erstellt (Image #{resp?.Image?.Id}, Aktion {resp?.Action?.Status}).";
    }

    [McpServerTool, Description("Delete a Hetzner Cloud snapshot/image by its numeric ID. This is irreversible.")]
    public static async Task<string> HetznerDeleteSnapshot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Snapshot/image numeric ID")] long imageId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_delete_snapshot");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        await hetzner.DeleteImageAsync(imageId);
        return $"Snapshot #{imageId} gelöscht.";
    }

    [McpServerTool, Description("Enable Hetzner's automated daily backups for a server (by ID or name). Adds a 20% surcharge on the server price.")]
    public static async Task<string> HetznerEnableBackups(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_enable_backups", server,
            (h, id) => h.EnableBackupsAsync(id), "Backups aktiviert");

    [McpServerTool, Description("Disable Hetzner's automated backups for a server (by ID or name). Existing backups are deleted.")]
    public static async Task<string> HetznerDisableBackups(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server)
        => await ActionAsync(httpContextAccessor, permissionService, hetzner, config, "hetzner_disable_backups", server,
            (h, id) => h.DisableBackupsAsync(id), "Backups deaktiviert");

    // ─────────────────────────── Resize (write) ───────────────────────────

    [McpServerTool, Description("Change (resize) a Hetzner Cloud server's type, e.g. 'cx32'. The server should be powered off first. upgradeDisk=true also grows the disk (then a downgrade is no longer possible).")]
    public static async Task<string> HetznerChangeServerType(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        IHetznerService hetzner, HetznerConfigService config,
        [Description("Hetzner server numeric ID or name")] string server,
        [Description("Target server type name, e.g. cx32")] string serverType,
        [Description("Also upgrade the disk (irreversible). Default false.")] bool upgradeDisk = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_change_server_type");
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";
        if (s.Status != "off")
            return $"{s.Name} ist im Status '{s.Status}'. Für einen Typ-Wechsel muss der Server ausgeschaltet sein (erst hetzner_shutdown).";

        var action = await hetzner.ChangeServerTypeAsync(s.Id, serverType, upgradeDisk);
        return $"{s.Name} wird auf Typ '{serverType}' geändert (Aktion {action?.Status}).";
    }

    // ─────────────────────────── Helpers ───────────────────────────

    private static async Task<string> ActionAsync(
        IHttpContextAccessor http, McpPermissionService perm, IHetznerService hetzner, HetznerConfigService config,
        string toolName, string server, Func<IHetznerService, long, Task<HetznerAction?>> action, string verb)
    {
        var denied = McpPermissionCheck.CheckAccess(http, perm, toolName);
        if (denied != null) return denied;
        if (!config.IsEnabled) return Disabled;

        var s = await ResolveAsync(hetzner, server);
        if (s == null) return $"Hetzner-Server nicht gefunden: {server}";

        var result = await action(hetzner, s.Id);
        return $"{s.Name} (#{s.Id}): {verb}. (Aktion: {result?.Status ?? "ausgelöst"})";
    }

    private static async Task<HetznerServer?> ResolveAsync(IHetznerService hetzner, string identifier)
    {
        if (long.TryParse(identifier, out var id))
        {
            var byId = await hetzner.GetServerAsync(id);
            if (byId != null) return byId;
        }
        var all = await hetzner.ListServersAsync();
        return all.FirstOrDefault(s => s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(s => s.Id.ToString() == identifier);
    }

    private static string? LinkedServerName(ServerConfigService serverConfig, HetznerServer s)
    {
        if (string.IsNullOrEmpty(s.Ipv4)) return null;
        var match = serverConfig.GetServers()
            .FirstOrDefault(c => !string.IsNullOrEmpty(c.SshHost) && c.SshHost == s.Ipv4);
        return match?.Name;
    }

    private static string Gb(long? bytes) => bytes is null ? "?" : $"{bytes.Value / 1073741824.0:F1} GB";
}
