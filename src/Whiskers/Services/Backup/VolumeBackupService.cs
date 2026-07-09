using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Backup;

public class VolumeBackupService : IVolumeBackupService
{
    private readonly IHostCommandExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VolumeBackupService> _logger;
    private readonly string _backupDir;

    // Throwaway helper container for backup/restore, pinned by digest (supply-chain hardening) so a
    // moved 'alpine' tag can't swap in a different image. Re-pin via: docker manifest inspect alpine:3.22
    private const string BackupImage = "alpine:3.22@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    // Docker volume/container names and backup file names: must start alphanumeric, then
    // alphanumerics plus _ . - only. Rejects anything that could break out of the shell command.
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    private static string ValidateName(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeName.IsMatch(value))
            throw new ArgumentException($"Invalid {what}: '{value}'");
        return value;
    }

    public VolumeBackupService(
        IHostCommandExecutor executor,
        IServiceScopeFactory scopeFactory,
        ILogger<VolumeBackupService> logger,
        DataPathOptions? dataPaths = null)
    {
        _executor = executor;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _backupDir = (dataPaths ?? DataPathOptions.Default).BackupsDir;
    }

    public async Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null)
    {
        var sid = serverId ?? "local";
        ValidateName(volumeName, "volume name");
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{volumeName}_{timestamp}.tar.gz";

        // Ensure backup directory exists
        await _executor.ExecuteAsync(sid, $"mkdir -p {ShellUtils.Quote(_backupDir)}", TimeSpan.FromSeconds(5));

        // Create backup using a temporary alpine container
        var result = await _executor.ExecuteAsync(sid,
            $"docker run --rm -v {ShellUtils.Quote(volumeName + ":/data")} -v {ShellUtils.Quote(_backupDir + ":/backup")} {BackupImage} tar czf {ShellUtils.Quote("/backup/" + fileName)} -C /data . 2>&1",
            TimeSpan.FromMinutes(10));

        if (!result.Success)
            throw new Exception($"Backup failed: {result.Output} {result.Error}");

        // Get file size
        var sizeResult = await _executor.ExecuteAsync(sid,
            $"stat -c %s {ShellUtils.Quote(_backupDir + "/" + fileName)}",
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
        ValidateName(volume, "volume name");
        ValidateName(backup.FileName, "backup file name");

        var quotedFile = ShellUtils.Quote("/backup/" + backup.FileName);
        var quotedBackupRo = ShellUtils.Quote(_backupDir + ":/backup");

        // 1. Verify the archive exists and is a readable, intact gzip tar BEFORE touching the live volume.
        //    Without this, a missing/truncated/corrupt backup would still get past the wipe below and
        //    destroy the volume with nothing to restore.
        var verify = await _executor.ExecuteAsync(backup.ServerId,
            $"docker run --rm -v {quotedBackupRo}:ro {BackupImage} tar tzf {quotedFile} > /dev/null 2>&1",
            TimeSpan.FromMinutes(5));
        if (!verify.Success)
            throw new Exception($"Restore aborted: backup archive '{backup.FileName}' is missing or unreadable — the target volume was left untouched.");

        // 2. Take an automatic safety backup of the current volume state so the wipe is reversible.
        try
        {
            await BackupVolumeAsync(volume, "", backup.ServerId, $"pre-restore safety ({backup.FileName})");
        }
        catch (Exception ex)
        {
            throw new Exception($"Restore aborted: could not create a pre-restore safety backup of '{volume}' ({ex.Message}). The target volume was left untouched.");
        }

        // 3. Clear the volume (including dotfiles, which `rm -rf /data/*` would leave behind) and extract.
        //    The inner `sh -c` script is single-quoted for the host shell; the file name is validated above.
        var innerScript = $"find /data -mindepth 1 -delete && tar xzf {quotedFile} -C /data";
        var result = await _executor.ExecuteAsync(backup.ServerId,
            $"docker run --rm -v {ShellUtils.Quote(volume + ":/data")} -v {quotedBackupRo} alpine sh -c {ShellUtils.Quote(innerScript)} 2>&1",
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
        ValidateName(backup.FileName, "backup file name");
        await _executor.ExecuteAsync(backup.ServerId,
            $"rm -f {ShellUtils.Quote(_backupDir + "/" + backup.FileName)}",
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
