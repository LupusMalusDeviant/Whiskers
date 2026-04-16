using ServerWatch.Models;

namespace ServerWatch.Services.Backup;

public interface IVolumeBackupService
{
    Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null);
    Task RestoreVolumeAsync(string backupId, string? targetVolume = null);
    Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null);
    Task DeleteBackupAsync(string backupId);
    Task<List<string>> ListVolumesAsync(string? serverId = null);
}
