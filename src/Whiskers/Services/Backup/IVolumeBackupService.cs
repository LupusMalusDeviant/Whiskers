using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Backup;

/// <summary>Volume backups on a Docker host. An omitted <c>serverId</c> means Whiskers' own host (<see cref="OmittedServer.Local"/>):
/// the implementation reads that from this declaration (<see cref="OmittedServer.For{TService}"/>), as the scope guard does.</summary>
[OmittedServer(OmittedServer.Local)]
public interface IVolumeBackupService
{
    [MutatingOperation("volume.backup")]
    Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null);
    [MutatingOperation("volume.restore")]
    Task RestoreVolumeAsync(string backupId, string? targetVolume = null);
    Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null);
    [MutatingOperation("volume.backup_delete")]
    Task DeleteBackupAsync(string backupId);
    Task<List<string>> ListVolumesAsync(string? serverId = null);

    /// <summary>The volumes of a server with their driver and labels (<c>docker volume ls --format json</c>): the
    /// inventory the gateway resolves a <c>volume.backup</c> against (contract 0012, D5). Unlike
    /// <see cref="ListVolumesAsync"/> a listing that cannot be made throws — a resolution must never read "no volumes"
    /// into a failed read.</summary>
    Task<IReadOnlyList<VolumeInfo>> ListVolumeInfoAsync(string? serverId, CancellationToken ct = default);
}

/// <summary>A Docker volume as <c>docker volume ls</c> lists it.</summary>
public sealed record VolumeInfo(string Name, string Driver, IReadOnlyDictionary<string, string> Labels);
