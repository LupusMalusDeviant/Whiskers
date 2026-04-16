using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;
using ServerWatch.Services.Server;

namespace ServerWatch.Services.Backup;

public class VolumeBackupService : IVolumeBackupService
{
    private readonly IHostCommandExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VolumeBackupService> _logger;
    private const string BackupDir = "/app/data/backups";

    public VolumeBackupService(
        IHostCommandExecutor executor,
        IServiceScopeFactory scopeFactory,
        ILogger<VolumeBackupService> logger)
    {
        _executor = executor;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null)
    {
        var sid = serverId ?? "local";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{volumeName}_{timestamp}.tar.gz";

        // Ensure backup directory exists
        await _executor.ExecuteAsync(sid, $"mkdir -p {BackupDir}", TimeSpan.FromSeconds(5));

        // Create backup using a temporary alpine container
        var result = await _executor.ExecuteAsync(sid,
            $"docker run --rm -v {volumeName}:/data -v {BackupDir}:/backup alpine tar czf /backup/{fileName} -C /data . 2>&1",
            TimeSpan.FromMinutes(10));

        if (!result.Success)
            throw new Exception($"Backup failed: {result.Output} {result.Error}");

        // Get file size
        var sizeResult = await _executor.ExecuteAsync(sid,
            $"stat -c %s {BackupDir}/{fileName}",
            TimeSpan.FromSeconds(5));

        long sizeBytes = 0;
        if (sizeResult.Success)
            long.TryParse(sizeResult.Output.Trim(), out sizeBytes);

        // Save metadata
        var entity = new VolumeBackupEntity
        {
            VolumeName = volumeName,
            ContainerName = containerName,
            ServerId = sid,
            FileName = fileName,
            SizeBytes = sizeBytes,
            Notes = notes
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.VolumeBackups.Add(entity);
        await db.SaveChangesAsync();

        _logger.LogInformation("Volume backup created: {Volume} → {File} ({Size} bytes)", volumeName, fileName, sizeBytes);
        return entity;
    }

    public async Task RestoreVolumeAsync(string backupId, string? targetVolume = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var backup = await db.VolumeBackups.FirstOrDefaultAsync(b => b.BackupId == backupId)
            ?? throw new Exception($"Backup '{backupId}' not found.");

        var volume = targetVolume ?? backup.VolumeName;

        var result = await _executor.ExecuteAsync(backup.ServerId,
            $"docker run --rm -v {volume}:/data -v {BackupDir}:/backup alpine sh -c \"rm -rf /data/* && tar xzf /backup/{backup.FileName} -C /data\" 2>&1",
            TimeSpan.FromMinutes(10));

        if (!result.Success)
            throw new Exception($"Restore failed: {result.Output} {result.Error}");

        _logger.LogInformation("Volume restored: {File} → {Volume}", backup.FileName, volume);
    }

    public async Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<VolumeBackupEntity> query = db.VolumeBackups;

        if (!string.IsNullOrEmpty(serverId))
            query = query.Where(b => b.ServerId == serverId);
        if (!string.IsNullOrEmpty(volumeName))
            query = query.Where(b => b.VolumeName == volumeName);

        return await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
    }

    public async Task DeleteBackupAsync(string backupId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var backup = await db.VolumeBackups.FirstOrDefaultAsync(b => b.BackupId == backupId)
            ?? throw new Exception($"Backup '{backupId}' not found.");

        // Delete file
        await _executor.ExecuteAsync(backup.ServerId,
            $"rm -f {BackupDir}/{backup.FileName}",
            TimeSpan.FromSeconds(5));

        // Delete metadata
        db.VolumeBackups.Remove(backup);
        await db.SaveChangesAsync();

        _logger.LogInformation("Backup deleted: {BackupId} ({File})", backupId, backup.FileName);
    }

    public async Task<List<string>> ListVolumesAsync(string? serverId = null)
    {
        var sid = serverId ?? "local";
        // Use label-aware format to show compose project + service for readability
        var result = await _executor.ExecuteAsync(sid,
            "docker volume ls --format '{{.Name}}'",
            TimeSpan.FromSeconds(10));

        if (!result.Success)
            return new List<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
