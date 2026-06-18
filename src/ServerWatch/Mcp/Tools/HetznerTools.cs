using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Services.Cloud;
using ServerWatch.Services.Hetzner;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

/// <summary>
/// Hetzner-only capabilities that have no Hostinger equivalent. Each tool takes a ServerWatch server
/// (by name or id) whose configured provider must be Hetzner; the per-server token is resolved
/// automatically. For provider-agnostic power/snapshot/metrics, use the cloud_* tools.
/// </summary>
[McpServerToolType]
public class HetznerTools
{
    [McpServerTool, Description("Enable Hetzner rescue mode on a server (by ServerWatch name or id), then it must be reset to boot into rescue. Returns the temporary root password. Recovery when the OS won't boot.")]
    public static async Task<string> HetznerEnableRescue(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_enable_rescue");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        var resp = await hetzner.EnableRescueAsync(ctx.Value.token, ctx.Value.server.Id);
        return $"Rescue-Mode für {ctx.Value.server.Name} aktiviert. Jetzt cloud_hard_reset ausführen, um hineinzubooten.\n" +
               $"Temporäres root-Passwort: {resp?.RootPassword ?? "(nicht zurückgegeben)"}";
    }

    [McpServerTool, Description("Disable Hetzner rescue mode on a server (by ServerWatch name or id).")]
    public static async Task<string> HetznerDisableRescue(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_disable_rescue");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        await hetzner.DisableRescueAsync(ctx.Value.token, ctx.Value.server.Id);
        return $"Rescue-Mode für {ctx.Value.server.Name} deaktiviert.";
    }

    [McpServerTool, Description("Enable Hetzner automated daily backups for a server (by ServerWatch name or id). Adds ~20% to the server price.")]
    public static async Task<string> HetznerEnableBackups(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_enable_backups");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        await hetzner.EnableBackupsAsync(ctx.Value.token, ctx.Value.server.Id);
        return $"Backups für {ctx.Value.server.Name} aktiviert.";
    }

    [McpServerTool, Description("Disable Hetzner automated backups for a server (by ServerWatch name or id). Existing backups are deleted.")]
    public static async Task<string> HetznerDisableBackups(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_disable_backups");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        await hetzner.DisableBackupsAsync(ctx.Value.token, ctx.Value.server.Id);
        return $"Backups für {ctx.Value.server.Name} deaktiviert.";
    }

    [McpServerTool, Description("Change (resize) a Hetzner server's type, e.g. 'cx32' (by ServerWatch name or id). The server must be powered off first. upgradeDisk=true also grows the disk (then a downgrade is no longer possible).")]
    public static async Task<string> HetznerChangeServerType(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server,
        [Description("Target server type name, e.g. cx32")] string serverType,
        [Description("Also upgrade the disk (irreversible). Default false.")] bool upgradeDisk = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_change_server_type");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        if (ctx.Value.server.Status != "off")
            return $"{ctx.Value.server.Name} ist '{ctx.Value.server.Status}'. Für einen Typ-Wechsel muss der Server aus sein (erst cloud_shutdown).";

        var action = await hetzner.ChangeServerTypeAsync(ctx.Value.token, ctx.Value.server.Id, serverType, upgradeDisk);
        return $"{ctx.Value.server.Name} wird auf Typ '{serverType}' geändert (Aktion {action?.Status}).";
    }

    [McpServerTool, Description("List Hetzner snapshots in the account of a given ServerWatch server (by name or id).")]
    public static async Task<string> HetznerListSnapshots(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server)")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_list_snapshots");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        var images = await hetzner.ListSnapshotsAsync(ctx.Value.token);
        if (images.Count == 0) return "Keine Snapshots gefunden.";
        var lines = images.Select(i =>
            $"- #{i.Id} {i.Description ?? "(ohne Beschreibung)"} [{i.Status}] | {(i.ImageSize.HasValue ? $"{i.ImageSize:F1} GB" : "-")} | von: {i.CreatedFrom?.Name ?? "-"} | {i.Created}");
        return $"Snapshots ({images.Count}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Delete a Hetzner snapshot/image by its numeric ID, in the account of a given ServerWatch server. Irreversible.")]
    public static async Task<string> HetznerDeleteSnapshot(
        IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService,
        CloudControlService cloud, IHetznerService hetzner,
        [Description("ServerWatch server name or id (must be a Hetzner server, selects the account)")] string server,
        [Description("Snapshot/image numeric ID")] long imageId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "hetzner_delete_snapshot");
        if (denied != null) return denied;

        var ctx = await cloud.HetznerContextAsync(server);
        if (ctx == null) return NotHetzner(server);
        await hetzner.DeleteImageAsync(ctx.Value.token, imageId);
        return $"Snapshot #{imageId} gelöscht.";
    }

    private static string NotHetzner(string server)
        => $"'{server}' ist kein konfigurierter Hetzner-Server (Provider/Key prüfen, oder IP-Abgleich fehlgeschlagen).";
}
