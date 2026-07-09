using Whiskers.Models;

namespace Whiskers.Services.Backup;

/// <summary>The Core's default <see cref="IVolumeBackupService"/> for when the VolumeBackups module is off.
/// The Scheduler module's <c>TaskExecutor</c> injects <see cref="IVolumeBackupService"/> (for VolumeBackup
/// tasks and their retention), so this default keeps that singleton graph resolvable — without it,
/// <c>ValidateOnBuild</c> would fail to construct <c>TaskExecutor</c> when Scheduler is on but VolumeBackups
/// is off. When the module is enabled the real <see cref="VolumeBackupService"/> is registered afterwards and
/// wins (last registration). Soft-dependency-via-no-op-Core-contract pattern (RoadToSAP §2.1).
///
/// Reads return empty. The mutating backup/restore operations deliberately <b>throw</b> rather than fake a
/// success: a scheduled VolumeBackup task then records a visible failure (caught by TaskExecutor) instead of
/// silently doing nothing, which would be a dangerous "backups look green but nothing was saved" state.</summary>
public sealed class NoopVolumeBackupService : IVolumeBackupService
{
    private const string Disabled =
        "The VolumeBackups module is disabled (set Features:volumebackups:Enabled=true to enable volume backups).";

    public Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null)
        => throw new InvalidOperationException(Disabled);

    public Task RestoreVolumeAsync(string backupId, string? targetVolume = null)
        => throw new InvalidOperationException(Disabled);

    public Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null)
        => Task.FromResult(new List<VolumeBackupEntity>());

    public Task DeleteBackupAsync(string backupId) => Task.CompletedTask;

    public Task<List<string>> ListVolumesAsync(string? serverId = null) => Task.FromResult(new List<string>());
}
