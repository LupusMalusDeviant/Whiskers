using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Backup;

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
