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
}
