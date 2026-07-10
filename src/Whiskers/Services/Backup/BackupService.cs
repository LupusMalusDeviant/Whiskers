using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Backup;

/// <summary>
/// Creates and restores consistent snapshots of Whiskers' own <c>/app/data</c> (F3). Runs IN-PROCESS
/// (System.Formats.Tar + GZip) — unlike <see cref="VolumeBackupService"/> which shells a helper container for
/// foreign Docker volumes — because the app can read its own data directory directly. The SQLite database is
/// captured with <c>VACUUM INTO</c> (a consistent online snapshot with the WAL folded in), the archive is
/// optionally AES-256-GCM encrypted with a VAULT_KEY-derived key, and restore is a crash-safe deferred swap.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataPathOptions _dataPaths;
    private readonly IMaintenanceStateService _maintenance;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BackupService> _logger;
    private readonly string? _vaultKey;
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    // Backup ids and file names: must start alphanumeric, then alphanumerics plus _ . - only. Rejects any
    // path-traversal or separator character. Same shape as VolumeBackupService's guard.
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public BackupService(
        IServiceScopeFactory scopeFactory,
        IMaintenanceStateService maintenance,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<BackupService> logger,
        DataPathOptions? dataPaths = null)
    {
        _scopeFactory = scopeFactory;
        _maintenance = maintenance;
        _lifetime = lifetime;
        _logger = logger;
        _dataPaths = dataPaths ?? DataPathOptions.Default;
        _vaultKey = configuration["VAULT_KEY"] ?? Environment.GetEnvironmentVariable("VAULT_KEY");
    }

    // ---------------------------------------------------------------- create

    public async Task<SelfBackupInfo> CreateBackupAsync(string label, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var selfDir = _dataPaths.SelfBackupsDir;
        Directory.CreateDirectory(selfDir);

        // Build scratch lives UNDER backups/self (so it is excluded from the snapshot by the recursion guard).
        var buildDir = Path.Combine(selfDir, ".build", id);
        Directory.CreateDirectory(buildDir);

        try
        {
            string provider;
            string[] metricsMigrations;
            string[] identityMigrations;
            string? stagedDb = null;

            using (var scope = _scopeFactory.CreateScope())
            {
                var metrics = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                var idDb = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();

                var isSqlite = metrics.Database.IsSqlite();
                provider = isSqlite ? SelfBackupFormat.ProviderSqlite : SelfBackupFormat.ProviderPostgres;
                metricsMigrations = (await metrics.Database.GetAppliedMigrationsAsync(ct)).ToArray();
                identityMigrations = (await idDb.Database.GetAppliedMigrationsAsync(ct)).ToArray();

                if (isSqlite)
                {
                    // Consistent online snapshot: VACUUM INTO produces a compacted single file with the WAL
                    // folded in (no -wal/-shm sidecars) — exactly what belongs in the archive. Both DbContexts
                    // share this one physical file, so this captures metrics AND identity data together.
                    stagedDb = Path.Combine(buildDir, SelfBackupFormat.DbEntryName);
                    // VACUUM INTO's destination is a path literal that SQLite does NOT allow as a bound
                    // parameter, so it cannot go through the parameterized ExecuteSqlAsync. The path is
                    // app-controlled (our backups/self build dir, never user input) and single-quotes are
                    // doubled, so the raw statement is safe.
                    var escaped = stagedDb.Replace("'", "''");
#pragma warning disable EF1002 // unparameterizable VACUUM INTO target; app-controlled + quoted path
                    await metrics.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", ct);
#pragma warning restore EF1002
                }
            }

            var createdAt = DateTime.UtcNow;
            var manifest = new SelfBackupManifest
            {
                FormatVersion = SelfBackupFormat.CurrentVersion,
                AppVersion = AppVersion(),
                CreatedAtUtc = createdAt,
                Label = label,
                Provider = provider,
                MetricsMigrations = metricsMigrations,
                IdentityMigrations = identityMigrations
            };
            var manifestPath = Path.Combine(buildDir, SelfBackupFormat.ManifestEntryName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts), ct);

            // Assemble the archive entry list: manifest first (fast reads), the staged DB, then every /app/data
            // file that survives the exclude set.
            var entries = new List<PackEntry> { new(SelfBackupFormat.ManifestEntryName, manifestPath) };
            if (stagedDb is not null)
                entries.Add(new PackEntry(SelfBackupFormat.DbEntryName, stagedDb));
            entries.AddRange(EnumerateDataFiles(Path.GetFullPath(_dataPaths.RootDir)));

            var tmpTarGz = Path.Combine(buildDir, "archive.tar.gz");
            await using (var tarOut = File.Create(tmpTarGz))
                await BackupArchiver.PackAsync(tarOut, entries, ct);

            var encrypted = !string.IsNullOrEmpty(_vaultKey);
            var fileName = encrypted ? $"{id}.tar.gz.enc" : $"{id}.tar.gz";
            var finalPath = Path.Combine(selfDir, fileName);

            if (encrypted)
            {
                await using var src = File.OpenRead(tmpTarGz);
                await using var dst = File.Create(finalPath);
                await BackupArchiveCipher.EncryptAsync(src, dst, _vaultKey!, ct);
            }
            else
            {
                File.Move(tmpTarGz, finalPath, overwrite: true);
            }

            var info = new SelfBackupInfo
            {
                Id = id,
                FileName = fileName,
                SizeBytes = new FileInfo(finalPath).Length,
                CreatedAtUtc = createdAt,
                Label = label,
                Provider = provider,
                Encrypted = encrypted,
                FormatVersion = SelfBackupFormat.CurrentVersion
            };
            await WriteSidecarAsync(selfDir, id, info, ct);

            _logger.LogInformation("Self-backup created: {Id} ({Size} bytes, encrypted={Encrypted}, provider={Provider})",
                id, info.SizeBytes, encrypted, provider);
            if (!encrypted)
                _logger.LogWarning("Self-backup {Id} is NOT encrypted (VAULT_KEY is not set) — it contains plaintext secrets; store it securely.", id);

            return info;
        }
        finally
        {
            TryDeleteDir(buildDir);
        }
    }

    // ---------------------------------------------------------------- list / resolve / delete / prune

    public async Task<IReadOnlyList<SelfBackupInfo>> ListBackupsAsync(CancellationToken ct = default)
    {
        var dir = _dataPaths.SelfBackupsDir;
        if (!Directory.Exists(dir)) return Array.Empty<SelfBackupInfo>();

        var list = new List<SelfBackupInfo>();
        foreach (var meta in Directory.EnumerateFiles(dir, "*.meta.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<SelfBackupInfo>(await File.ReadAllTextAsync(meta, ct), JsonOpts);
                if (info is null || string.IsNullOrEmpty(info.FileName)) continue;
                var archive = Path.Combine(dir, info.FileName);
                if (!File.Exists(archive)) continue;
                info.SizeBytes = new FileInfo(archive).Length;   // trust the file, not a possibly-stale sidecar
                list.Add(info);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable backup sidecar {File}", meta);
            }
        }
        return list.OrderByDescending(b => b.CreatedAtUtc).ToList();
    }

    public string? ResolveArchivePath(string id)
    {
        if (!SafeName.IsMatch(id)) return null;
        var dirFull = Path.GetFullPath(_dataPaths.SelfBackupsDir);
        foreach (var name in new[] { $"{id}.tar.gz.enc", $"{id}.tar.gz" })
        {
            var full = Path.GetFullPath(Path.Combine(dirFull, name));
            if (!full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public Task DeleteBackupAsync(string id, CancellationToken ct = default)
    {
        if (!SafeName.IsMatch(id)) throw new ArgumentException($"Invalid backup id '{id}'.");
        var archive = ResolveArchivePath(id);
        if (archive is not null) File.Delete(archive);
        var sidecar = Path.Combine(_dataPaths.SelfBackupsDir, $"{id}.meta.json");
        if (File.Exists(sidecar)) File.Delete(sidecar);
        _logger.LogInformation("Self-backup deleted: {Id}", id);
        return Task.CompletedTask;
    }

    public async Task PruneAsync(int keepNewest, CancellationToken ct = default)
    {
        if (keepNewest < 0) return;
        var all = await ListBackupsAsync(ct);
        foreach (var old in all.Skip(keepNewest))
        {
            try { await DeleteBackupAsync(old.Id, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Retention: failed to delete backup {Id}", old.Id); }
        }
    }

    // ---------------------------------------------------------------- validate / restore

    public async Task<string> StageUploadAsync(Stream source, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dataPaths.SelfBackupsDir);
        var path = Path.Combine(_dataPaths.SelfBackupsDir, $".upload-{Guid.NewGuid():N}.tmp");
        await using var fs = File.Create(path);
        await source.CopyToAsync(fs, ct);
        return path;
    }

    public Task DiscardUploadAsync(string uploadPath, CancellationToken ct = default)
    {
        // Only ever delete an upload temp we created (inside SelfBackupsDir, named .upload-*.tmp) — never an
        // arbitrary path handed in from the UI.
        var dirFull = Path.GetFullPath(_dataPaths.SelfBackupsDir);
        var full = Path.GetFullPath(uploadPath);
        var name = Path.GetFileName(full);
        if (full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && name.StartsWith(".upload-", StringComparison.Ordinal)
            && name.EndsWith(".tmp", StringComparison.Ordinal))
            TryDeleteFile(full);
        return Task.CompletedTask;
    }

    public async Task<BackupValidationResult> ValidateArchiveAsync(string uploadedPath, CancellationToken ct = default)
    {
        string? decryptTemp = null;
        try
        {
            var prepared = await PrepareValidatedAsync(uploadedPath, ct);
            decryptTemp = prepared.DecryptTemp;
            return BackupValidationResult.Success(prepared.Manifest, prepared.Encrypted);
        }
        catch (BackupValidationException ex)
        {
            return BackupValidationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-backup validation failed");
            return BackupValidationResult.Fail("Backup validation failed: " + ex.Message);
        }
        finally
        {
            // Validation is read-only: never keep the decrypted temp around.
            if (decryptTemp is not null) TryDeleteFile(decryptTemp);
        }
    }

    public async Task BeginRestoreAsync(string uploadedPath, string actor, CancellationToken ct = default)
    {
        await _restoreLock.WaitAsync(ct);
        try
        {
            var prepared = await PrepareValidatedAsync(uploadedPath, ct);   // throws on any incompatibility

            // Enter maintenance so the pre-restore snapshot is quiescent and no doomed writes are accepted in
            // the seconds before the process stops. If we abort BEFORE the commit point, we exit maintenance so
            // the app is not stranded in 503; once committed we stop the process (a restart clears the flag).
            var committed = false;
            _maintenance.EnterMaintenance("Ein Backup wird wiederhergestellt. Whiskers startet gleich neu.");
            try
            {
                _logger.LogWarning("Restore initiated by {Actor} from a backup created {Created:o} (provider={Provider}).",
                    actor, prepared.Manifest.CreatedAtUtc, prepared.Manifest.Provider);

                // Automatic pre-restore safety backup (DB-safety rule). Abort the whole restore if it fails —
                // nothing has been changed at this point.
                try
                {
                    await CreateBackupAsync($"pre-restore-{DateTime.UtcNow:yyyyMMdd_HHmmss}", ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Restore aborted: could not create a pre-restore safety backup ({ex.Message}). Nothing was changed.", ex);
                }

                // Extract the already-decrypted archive into a FRESH staging dir. Staging stays immutable from
                // here until the boot handler consumes it.
                var staging = _dataPaths.RestoreStagingDir;
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                await using (var tarFs = File.OpenRead(prepared.TarGzPath))
                    await BackupArchiver.ExtractAsync(tarFs, staging, ct);

                // Commit point: write the marker atomically (.tmp + rename) AFTER staging is fully populated.
                var marker = new RestoreMarker
                {
                    StagingDir = Path.GetFullPath(staging),
                    CreatedAtUtc = DateTime.UtcNow,
                    SourceCreatedAtUtc = prepared.Manifest.CreatedAtUtc,
                    Provider = prepared.Manifest.Provider
                };
                var tmpMarker = _dataPaths.RestorePendingMarker + ".tmp";
                await File.WriteAllTextAsync(tmpMarker, JsonSerializer.Serialize(marker, JsonOpts), ct);
                File.Move(tmpMarker, _dataPaths.RestorePendingMarker, overwrite: true);
                committed = true;

                // The upload temp (if this restore came from an uploaded file rather than an on-box archive) has
                // served its purpose now that staging is committed — remove it. On-box archives (<id>.tar.gz)
                // do not match this prefix and are preserved.
                if (Path.GetFileName(uploadedPath).StartsWith(".upload-", StringComparison.Ordinal))
                    TryDeleteFile(uploadedPath);

                _logger.LogWarning("Restore staged and committed by {Actor}; stopping the application so the swap runs on restart.", actor);

                // Stop the process. Under Docker's restart policy (or systemd), the container comes back up and
                // RestoreBootHandler swaps the staged files before anything opens the DB.
                _lifetime.StopApplication();
            }
            catch when (!committed)
            {
                _maintenance.ExitMaintenance();   // failed before committing — resume normal service
                throw;
            }
            finally
            {
                if (prepared.DecryptTemp is not null) TryDeleteFile(prepared.DecryptTemp);
            }
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    /// <summary>Shared validation core: detects/decrypts the archive with the current VAULT_KEY, reads and
    /// checks the manifest (format/provider/schema) and enforces the extraction guards — all without touching
    /// live state. On success returns the decrypted tar.gz path (equal to the input when the archive was
    /// plaintext) plus the manifest; the caller owns cleanup of <c>DecryptTemp</c> when set.</summary>
    private async Task<PreparedRestore> PrepareValidatedAsync(string uploadedPath, CancellationToken ct)
    {
        bool encrypted;
        await using (var head = File.OpenRead(uploadedPath))
        {
            var buf = new byte[BackupArchiveCipher.MagicLength];
            var read = await head.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
            encrypted = read >= buf.Length && BackupArchiveCipher.StartsWithMagic(buf);
        }

        var tarGzPath = uploadedPath;
        string? decryptTemp = null;

        if (encrypted)
        {
            if (string.IsNullOrEmpty(_vaultKey))
                throw new BackupValidationException(
                    "This backup is encrypted, but VAULT_KEY is not set on this instance. Set VAULT_KEY to the value used when the backup was created, then retry.");

            Directory.CreateDirectory(_dataPaths.SelfBackupsDir);
            decryptTemp = Path.Combine(_dataPaths.SelfBackupsDir, $".decrypt-{Guid.NewGuid():N}.tar.gz");
            try
            {
                await using var src = File.OpenRead(uploadedPath);
                await using var dst = File.Create(decryptTemp);
                await BackupArchiveCipher.DecryptAsync(src, dst, _vaultKey!, ct);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
            {
                TryDeleteFile(decryptTemp);
                throw new BackupValidationException(
                    "Backup could not be decrypted — it was created with a different VAULT_KEY, or the file is corrupted.");
            }
            tarGzPath = decryptTemp;
        }

        try
        {
            BackupInspection inspection;
            try
            {
                await using var tarFs = File.OpenRead(tarGzPath);
                inspection = await BackupArchiver.InspectAsync(tarFs, _dataPaths.RestoreStagingDir, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new BackupValidationException($"Backup archive is invalid: {ex.Message}");
            }

            var manifest = inspection.Manifest
                ?? throw new BackupValidationException("Backup archive is missing its manifest.json — this is not a Whiskers self-backup.");

            if (manifest.FormatVersion > SelfBackupFormat.CurrentVersion)
                throw new BackupValidationException(
                    $"Backup format v{manifest.FormatVersion} is newer than this build supports (v{SelfBackupFormat.CurrentVersion}). Update Whiskers first.");

            using (var scope = _scopeFactory.CreateScope())
            {
                var metrics = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                var idDb = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();

                var currentProvider = metrics.Database.IsSqlite() ? SelfBackupFormat.ProviderSqlite : SelfBackupFormat.ProviderPostgres;
                if (!string.Equals(manifest.Provider, currentProvider, StringComparison.OrdinalIgnoreCase))
                    throw new BackupValidationException(
                        $"Backup was created on a '{manifest.Provider}' database but this instance uses '{currentProvider}'. Cross-provider restore is not supported.");

                CheckSubset(metrics.Database.GetMigrations(), manifest.MetricsMigrations, "metrics");
                CheckSubset(idDb.Database.GetMigrations(), manifest.IdentityMigrations, "identity");
            }

            return new PreparedRestore(tarGzPath, manifest, encrypted, decryptTemp);
        }
        catch
        {
            if (decryptTemp is not null) TryDeleteFile(decryptTemp);
            throw;
        }
    }

    private static void CheckSubset(IEnumerable<string> known, IEnumerable<string> applied, string which)
    {
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var missing = applied.Where(a => !knownSet.Contains(a)).ToList();
        if (missing.Count > 0)
            throw new BackupValidationException(
                $"Backup was taken on a newer {which} schema than this build knows ({missing.Count} unknown migration(s), e.g. '{missing[0]}'). " +
                "Restoring it would require a schema downgrade, which is not supported — update Whiskers to at least the backup's version first.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Yields every file under <paramref name="rootFull"/> that belongs in a snapshot, skipping the
    /// backups/ tree (recursion), the live SQLite files (the VACUUM copy replaces them), the restore
    /// staging/marker, JsonFileStore *.tmp transients and the anticipated vault.key.</summary>
    private IEnumerable<PackEntry> EnumerateDataFiles(string rootFull)
    {
        var excludedDirs = new[]
        {
            Path.GetFullPath(_dataPaths.BackupsDir),
            Path.GetFullPath(_dataPaths.RestoreStagingDir)
        };
        var excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(_dataPaths.DbPath),
            Path.GetFullPath(_dataPaths.DbPath + "-wal"),
            Path.GetFullPath(_dataPaths.DbPath + "-shm"),
            Path.GetFullPath(_dataPaths.RestorePendingMarker),
            Path.GetFullPath(_dataPaths.RestorePendingMarker + ".corrupt"),
            Path.GetFullPath(Path.Combine(rootFull, "vault.key"))
        };

        if (!Directory.Exists(rootFull)) yield break;

        var stack = new Stack<string>();
        stack.Push(rootFull);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var subFull = Path.GetFullPath(sub);
                if (excludedDirs.Any(x => PathEquals(subFull, x))) continue;
                stack.Push(subFull);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fileFull = Path.GetFullPath(file);
                if (excludedFiles.Contains(fileFull)) continue;
                if (fileFull.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(rootFull, fileFull).Replace('\\', '/');
                yield return new PackEntry(rel, fileFull);
            }
        }
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private async Task WriteSidecarAsync(string selfDir, string id, SelfBackupInfo info, CancellationToken ct)
    {
        var sidecar = Path.Combine(selfDir, $"{id}.meta.json");
        var tmp = sidecar + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(info, JsonOpts), ct);
        File.Move(tmp, sidecar, overwrite: true);
    }

    private static string AppVersion()
        => typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(BackupService).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private readonly record struct PreparedRestore(string TarGzPath, SelfBackupManifest Manifest, bool Encrypted, string? DecryptTemp);
}
