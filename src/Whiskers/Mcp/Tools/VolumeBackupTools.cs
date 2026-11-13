using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

/// <summary>Read-only view of Docker volumes and their backups (Plan-0013 WP4).
///
/// <para>Creating a backup would be a defensible write tool; restoring one is not — a restore overwrites live
/// data, and this project's standing rule is that destructive data operations need proven, verifiable safety
/// before they are automated at all, let alone handed to an agent. Both stay out until that is decided
/// deliberately (WP4.2).</para></summary>
[McpServerToolType]
public class VolumeBackupTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the Docker volume backups Whiskers has taken: volume, owning container, server, size and age. Answers 'when was this volume last backed up?'. Read-only — this cannot create or restore a backup.")]
    public static async Task<string> ListVolumeBackups(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IVolumeBackupService backups,
        [Description("Server ID to filter by (optional, omit for all servers)")] string? serverId = null,
        [Description("Volume name to filter by (optional)")] string? volumeName = null,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_volume_backups");
        if (denied != null) return denied;

        var entries = await backups.ListBackupsAsync(serverId, volumeName, cancellationToken);
        if (entries.Count == 0)
            return volumeName is null
                ? "No volume backups found."
                : $"No backups found for volume '{volumeName}'.";

        // Age, not just the timestamp: "14 days old" is the answer to the question actually being asked.
        var now = DateTime.UtcNow;
        var lines = entries
            .OrderByDescending(b => b.CreatedAt)
            .Select(b =>
            {
                var age = now - b.CreatedAt;
                var ageText = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago" : $"{(int)age.TotalHours}h ago";
                var notes = string.IsNullOrWhiteSpace(b.Notes) ? "" : $" | {b.Notes}";
                return $"- {b.VolumeName} [{b.BackupId}] | container: {b.ContainerName} | server: {b.ServerId} | " +
                       $"{b.SizeBytes / 1024.0 / 1024.0:F1} MiB | {b.CreatedAt:yyyy-MM-dd HH:mm} UTC ({ageText}){notes}";
            });

        return $"Volume backups ({entries.Count}):\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the Docker volumes on a server, so a backup gap can be spotted by comparing this against list_volume_backups. Read-only.")]
    public static async Task<string> ListVolumes(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IVolumeBackupService backups,
        [Description("Server ID (optional, omit for the default server)")] string? serverId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_volumes");
        if (denied != null) return denied;

        var volumes = await backups.ListVolumesAsync(serverId, cancellationToken);
        if (volumes.Count == 0) return "No Docker volumes found.";

        return $"Docker volumes ({volumes.Count}):\n{string.Join('\n', volumes.Select(v => $"- {v}"))}";
    }
}
