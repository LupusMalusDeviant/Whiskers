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
/// captured with <c>VACUUM INTO</c> (a consistent online snapshot with the WAL folded in), a PostgreSQL database with
/// <c>pg_dump</c> (R1-33, <see cref="PostgresDumpTool"/>; restored at the start in one transaction), the archive is
/// optionally AES-256-GCM encrypted with a VAULT_KEY-derived key, and restore is a crash-safe deferred swap.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Whiskers.Services.Vault.IVaultService? _vault;
    private readonly DataPathOptions _dataPaths;
    private readonly IMaintenanceStateService _maintenance;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BackupService> _logger;
    private readonly string? _vaultKey;
    private readonly PostgresDumpTool _pgTools;
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    // Backup ids and file names: must start alphanumeric, then alphanumerics plus _ . - only. Rejects any
    // path-traversal or separator character. Same shape as VolumeBackupService's guard.
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public BackupService(
        IServiceScopeFactory scopeFactory,
        IMaintenanceStateService maintenance,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<BackupService> logger,
        DataPathOptions? dataPaths = null,
        Whiskers.Services.Vault.IVaultService? vault = null,
        PostgresDumpTool? pgTools = null)
    {
        _vault = vault;
        _pgTools = pgTools ?? PostgresDumpTool.FromEnvironment();
        _scopeFactory = scopeFactory;
        _maintenance = maintenance;
        _lifetime = lifetime;
        _logger = logger;
        _dataPaths = dataPaths ?? DataPathOptions.Default;
        _vaultKey = SecretSource.VaultKey(configuration);
    }

    /// <summary>Label prefix of the backups taken before a schema upgrade (R1-34). The scheduled retention skips them.</summary>
    public const string PreUpgradeLabelPrefix = "pre-upgrade-";

    private const string CommittedRestorePending =
        "A restore is already committed and is applied at the next start. Restart Whiskers first, or remove .restore-pending from the data directory to give it up.";

    /// <summary>Bounds on what one archive may unpack to (R1-32).</summary>
    public BackupExtractionLimits ExtractionLimits { get; init; } = BackupExtractionLimits.Default;

    // ---------------------------------------------------------------- create

    public Task<SelfBackupInfo> CreateBackupAsync(string label, CancellationToken ct = default) => CreateCoreAsync(label, ct);

    /// <summary>Contract 0015 D9 (start-up only, like S-09): the <c>pre-rotation</c> / <c>post-rotation</c> backup of a
    /// <c>VAULT_KEY</c> rotation, taken before the host and the Mutation Gateway exist. Its only caller is
    /// <see cref="Whiskers.Services.Vault.VaultKeyRotationBootHandler"/> (pinned by VaultKeyRotationTests).</summary>
    internal Task<SelfBackupInfo> CreateVaultRotationBackupAsync(string label, CancellationToken ct = default) =>
        CreateCoreAsync(label, ct);

    /// <inheritdoc />
    public Task<SelfBackupInfo> CreatePreUpgradeBackupAsync(CancellationToken ct = default) =>
        CreateCoreAsync($"{PreUpgradeLabelPrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss}", ct);

    /// <inheritdoc />
    public async Task DeletePreUpgradeBackupAsync(string id, CancellationToken ct = default)
    {
        var backup = (await ListBackupsAsync(ct)).FirstOrDefault(b => b.Id == id);
        if (backup is null || !backup.Label.StartsWith(PreUpgradeLabelPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{id}' is not a pre-upgrade backup; start-up deletes only those.");
        await DeleteCoreAsync(id);
    }

    /// <inheritdoc />
    public Task BeginHostRestoreAsync(string backupId, string actor, RestoreTarget target, CancellationToken ct = default)
    {
        var archive = ResolveArchivePath(backupId)
                      ?? throw new BackupValidationException($"No self-backup with the id '{backupId}' in this data directory.");
        return BeginRestoreCoreAsync(archive, actor, target, ct);
    }

    private async Task<SelfBackupInfo> CreateCoreAsync(string label, CancellationToken ct)
    {
        // R1-28: the archive key is VAULT_KEY. When the vault rejected it, it is a mistyped or changed key — an archive
        // sealed with it would need that very typo to restore, and the panel would call it encrypted. No backup then.
        if (_vault?.KeyState == Whiskers.Services.Vault.VaultKeyState.Wrong)
        {
            _logger.LogError("Self-backup refused: VAULT_KEY does not match the vault, an archive would be sealed with a wrong key");
            throw new InvalidOperationException(
                "Self-backup refused: VAULT_KEY does not match the vault. Set the right key and restart, then back up.");
        }

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
            string? stagedDump = null;

            // R1-23: the audit checkpoint file is taken BEFORE the database. A checkpoint reaches the file only after
            // the database has it, so the restored database always reaches the restored checkpoint; taken after,
            // a checkpoint written in between would make the restored trail look cut off.
            string? stagedCheckpoint = null;
            if (File.Exists(_dataPaths.AuditCheckpointJson))
            {
                stagedCheckpoint = Path.Combine(buildDir, Path.GetFileName(_dataPaths.AuditCheckpointJson));
                for (var attempt = 1; ; attempt++)
                {
                    try { File.Copy(_dataPaths.AuditCheckpointJson, stagedCheckpoint, overwrite: true); break; }
                    catch (IOException) when (attempt < 3) { await Task.Delay(50, ct); }   // being replaced right now
                }
            }

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
                else
                {
                    // R1-33: the database itself, consistent in one snapshot (pg_dump reads in one transaction); both
                    // contexts share its public schema. A dump that fails fails the backup: an archive without the
                    // database would claim a recovery point it does not hold.
                    stagedDump = Path.Combine(buildDir, SelfBackupFormat.PgDumpEntryName);
                    await _pgTools.DumpAsync(metrics.Database.GetConnectionString()
                        ?? throw new InvalidOperationException("The PostgreSQL connection string is not known."), stagedDump, ct);
                }
            }

            var createdAt = DateTime.UtcNow;
            var manifest = new SelfBackupManifest
            {
                FormatVersion = stagedDump is null ? SelfBackupFormat.SqliteVersion : SelfBackupFormat.PostgresDumpVersion,
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
            if (stagedDump is not null)
                entries.Add(new PackEntry(SelfBackupFormat.PgDumpEntryName, stagedDump));
            if (stagedCheckpoint is not null)
                entries.Add(new PackEntry(
                    Path.GetRelativePath(Path.GetFullPath(_dataPaths.RootDir), Path.GetFullPath(_dataPaths.AuditCheckpointJson))
                        .Replace('\\', '/'),
                    stagedCheckpoint));
            entries.AddRange(EnumerateDataFiles(Path.GetFullPath(_dataPaths.RootDir)));

            var tmpTarGz = Path.Combine(buildDir, "archive.tar.gz");
            await using (var tarOut = File.Create(tmpTarGz))
                await BackupArchiver.PackAsync(tarOut, entries, ct);

            var encrypted = !string.IsNullOrEmpty(_vaultKey);
            var fileName = encrypted ? $"{id}.tar.gz.enc" : $"{id}.tar.gz";
            var finalPath = Path.Combine(selfDir, fileName);

            if (encrypted)
            {
                // R1-32: sealed in the build dir and published by one rename — an interrupted run leaves no
                // half-written archive under the final name.
                var tmpEnc = Path.Combine(buildDir, "archive.tar.gz.enc");
                await using (var src = File.OpenRead(tmpTarGz))
                await using (var dst = File.Create(tmpEnc))
                    await BackupArchiveCipher.EncryptAsync(src, dst, _vaultKey!, ct);
                tmpTarGz = tmpEnc;
            }

            var info = new SelfBackupInfo
            {
                Id = id,
                FileName = fileName,
                SizeBytes = new FileInfo(tmpTarGz).Length,
                CreatedAtUtc = createdAt,
                Label = label,
                Provider = provider,
                Encrypted = encrypted,
                FormatVersion = manifest.FormatVersion,
                IncludesDatabase = stagedDb is not null || stagedDump is not null
            };
            // The sidecar first: an archive is listed (and pruned) only through it, so a crash in between leaves a
            // sidecar whose archive is missing (skipped by the list), never an archive nobody sees.
            await WriteSidecarAsync(selfDir, id, info, ct);
            File.Move(tmpTarGz, finalPath, overwrite: true);

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
        // Contract 0015 (D9): archives sealed before the last VAULT_KEY rotation need the key they were sealed with.
        if (VaultRotationCutoff() is { } rotatedAt)
            foreach (var b in list) b.SealedWithEarlierKey = b.Encrypted && b.CreatedAtUtc < rotatedAt;
        return list.OrderByDescending(b => b.CreatedAtUtc).ToList();
    }

    /// <summary>When the last VAULT_KEY rotation finished (its fresh backup is the first under the new key), or null.</summary>
    private DateTime? VaultRotationCutoff()
    {
        try
        {
            return Whiskers.Services.Vault.VaultKeyRotation.ReadJournal(_dataPaths) is { Phase: Whiskers.Services.Vault.VaultKeyRotationJournal.Done } done
                ? done.CompletedAtUtc
                : null;
        }
        catch (Whiskers.Services.Vault.VaultKeyRotationException)
        {
            return null;
        }
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

    public Task DeleteBackupAsync(string id, CancellationToken ct = default) => DeleteCoreAsync(id);

    private Task DeleteCoreAsync(string id)
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
        // R1-34: the pre-upgrade backups are the way back from an upgrade — the scheduled retention would delete them
        // a few nights later. UpgradeGuard keeps its own count of them.
        var all = (await ListBackupsAsync(ct))
            .Where(b => !b.Label.StartsWith(PreUpgradeLabelPrefix, StringComparison.Ordinal));
        foreach (var old in all.Skip(keepNewest))
        {
            try { await DeleteCoreAsync(old.Id); }
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
        // Told before the confirm dialog, not after it (the restore itself refuses too).
        if (File.Exists(_dataPaths.RestorePendingMarker))
            return BackupValidationResult.Fail(CommittedRestorePending);
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

    public Task BeginRestoreAsync(string uploadedPath, string actor, CancellationToken ct = default) =>
        BeginRestoreCoreAsync(uploadedPath, actor, RestoreTarget.ThisRelease, ct);

    public Task BeginRestoreAsync(string uploadedPath, string actor, RestoreTarget target, CancellationToken ct = default) =>
        BeginRestoreCoreAsync(uploadedPath, actor, target, ct);

    private async Task BeginRestoreCoreAsync(string uploadedPath, string actor, RestoreTarget target, CancellationToken ct)
    {
        var forward = target == RestoreTarget.ThisRelease;
        await _restoreLock.WaitAsync(ct);
        try
        {
            // A committed restore waits for the restart; a second one would delete its staging and strand its marker.
            if (File.Exists(_dataPaths.RestorePendingMarker))
                throw new BackupValidationException(CommittedRestorePending);
            var prepared = await PrepareValidatedAsync(uploadedPath, ct);   // throws on any incompatibility
            var staging = _dataPaths.RestoreStagingDir;

            // Enter maintenance so the pre-restore snapshot is quiescent and no doomed writes are accepted in
            // the seconds before the process stops. If we abort BEFORE the commit point, we exit maintenance so
            // the app is not stranded in 503; once committed we stop the process (a restart clears the flag).
            var committed = false;
            _maintenance.EnterMaintenance("Ein Backup wird wiederhergestellt. Whiskers startet gleich neu.");
            try
            {
                _logger.LogWarning("Restore initiated by {Actor} from a backup created {Created:o} (provider={Provider}).",
                    actor, prepared.Manifest.CreatedAtUtc, prepared.Manifest.Provider);

                // Extract the already-decrypted archive into a FRESH staging dir and check what was extracted
                // (R1-32): database integrity and migrations, the JSON stores, the key ring. Staging stays immutable
                // from here until the boot handler consumes it; without the marker it is discarded at the next start.
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                EnsureFreeSpace(prepared.ExtractedBytes);
                try
                {
                    await using (var tarFs = File.OpenRead(prepared.TarGzPath))
                        await BackupArchiver.ExtractAsync(tarFs, staging, ExtractionLimits, ct);
                }
                catch (InvalidDataException ex)
                {
                    throw new BackupValidationException($"Backup archive is invalid: {ex.Message}");
                }
                StagedRestoreValidator.Validate(staging, prepared.Manifest);
                var stagedDump = Path.Combine(staging, SelfBackupFormat.PgDumpEntryName);
                var postgres = File.Exists(stagedDump);
                // A file that is no dump is refused before the safety backup costs a full backup.
                if (postgres) await CheckPostgresDumpReadableAsync(stagedDump, ct);
                // R1-34: for the previous release, this release's migrations are not run on the archive (the previous
                // release starts on the schema it knows); the history checks against the manifest stay.
                else if (forward) await ProbeMigrationAsync(Path.Combine(staging, SelfBackupFormat.DbEntryName), ct);

                // Automatic pre-restore safety backup (DB-safety rule). Abort the whole restore if it fails —
                // nothing has been changed at this point. On PostgreSQL it is taken BEFORE the dry run (R1-33): the dry
                // run executes the archive's SQL against the live database, and whatever a crafted script might get
                // past its rollback, the safety backup holds the state from before (a refused restore leaves one extra
                // archive).
                SelfBackupInfo safety;
                try
                {
                    safety = await CreateCoreAsync($"pre-restore-{DateTime.UtcNow:yyyyMMdd_HHmmss}", ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Restore aborted: could not create a pre-restore safety backup ({ex.Message}). Nothing was changed.", ex);
                }
                if (postgres) await ProbePostgresRestoreAsync(stagedDump, prepared.Manifest, safety.Id, forward, ct);

                // Commit point: write the marker atomically (.tmp + rename) AFTER staging is fully populated.
                var marker = new RestoreMarker
                {
                    StagingDir = Path.GetFullPath(staging),
                    CreatedAtUtc = DateTime.UtcNow,
                    SourceCreatedAtUtc = prepared.Manifest.CreatedAtUtc,
                    Provider = prepared.Manifest.Provider,
                    Actor = actor,
                    Archive = Path.GetFileName(uploadedPath),
                };
                var tmpMarker = _dataPaths.RestorePendingMarker + ".tmp";
                await File.WriteAllTextAsync(tmpMarker, JsonSerializer.Serialize(marker, JsonOpts), ct);
                File.Move(tmpMarker, _dataPaths.RestorePendingMarker, overwrite: true);
                committed = true;

                // R1-34: the restore ends an unfinished upgrade, and the restored data is no longer what the last release
                // left — the next start of a newer release is a new upgrade with its own backup, also without a pending
                // schema migration. The same for the Settings panel and the CLI. Best effort: the restore is staged.
                ResetUpgradeState();

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
                TryDeleteDir(staging);            // never committed: nothing may be left to swap
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

    private void ResetUpgradeState()
    {
        foreach (var file in new[] { _dataPaths.UpgradePendingMarker, _dataPaths.LastStartedVersionFile })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{File} could not be deleted after the restore was staged; the next start may not " +
                    "take a pre-upgrade backup of the restored data.", Path.GetFileName(file));
            }
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

        // R1-32: with a VAULT_KEY every archive this instance writes is sealed with it. An unsealed one is not
        // authenticated — anybody who can hand over a file could choose the roles, keys and settings restored.
        if (!encrypted && !string.IsNullOrEmpty(_vaultKey))
            throw new BackupValidationException(
                "This backup is not encrypted. With VAULT_KEY set only encrypted backups made with that key are restored, because an unencrypted file cannot be checked for changes.");

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
                inspection = await BackupArchiver.InspectAsync(tarFs, _dataPaths.RestoreStagingDir, ExtractionLimits, ct);
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

                // R1-32: the database is replaced, never removed — an archive must hold the database of its provider
                // and no other. R1-33: a PostgreSQL archive from before the database dump holds the files only; paired
                // with the database as it is now, they would restore a state that never existed — not restored.
                var sqlite = string.Equals(manifest.Provider, SelfBackupFormat.ProviderSqlite, StringComparison.OrdinalIgnoreCase);
                if (sqlite && (!inspection.HasDatabase || inspection.HasDatabaseDump))
                    throw new BackupValidationException(inspection.HasDatabaseDump
                        ? "Backup archive is invalid: it was made on SQLite but holds a PostgreSQL dump."
                        : "Backup archive is invalid: it was made on SQLite but holds no database.");
                if (!sqlite && inspection.HasDatabase)
                    throw new BackupValidationException("Backup archive is invalid: it was made on PostgreSQL but holds a SQLite database.");
                if (!sqlite && !inspection.HasDatabaseDump)
                    throw new BackupValidationException(
                        "This backup holds the files only, not the PostgreSQL database (it was made before database dumps were included). " +
                        "It is not restored: its files would meet a database from another time. Restore the database and the files together from a newer backup.");
            }

            return new PreparedRestore(tarGzPath, manifest, encrypted, decryptTemp, inspection.ExtractedBytes);
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
            Path.GetFullPath(_dataPaths.RestoreAppliedNote),   // a restore's pending audit record belongs to this install
            Path.GetFullPath(_dataPaths.AuditCheckpointJson),   // staged before the database copy instead
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
                // R1-32: only what a restore may write back (SelfBackupLayout) — the same list on both sides.
                if (SelfBackupLayout.Classify(rel, isDirectory: false) is not (BackupEntryKind.File or BackupEntryKind.Directory)) continue;
                // R1-35: a JSON store's previous generation and its unreadable copies are no live data. Only in the two
                // store directories (the root-level ones are not in the layout): elsewhere, e.g. an uploaded SSH key
                // named "*.bak", the name is the operator's own.
                if (IsStoreLeftover(rel)) continue;
                yield return new PackEntry(rel, fileFull);
            }
        }
    }

    private bool IsStoreLeftover(string rel)
    {
        var top = rel.Split('/')[0];
        if (top != Path.GetFileName(_dataPaths.AgentChatDir) && top != Path.GetFileName(_dataPaths.ChatDir)) return false;
        var name = Path.GetFileName(rel);
        return name.EndsWith(".json" + Whiskers.Services.Persistence.DurableFile.BackupSuffix, StringComparison.Ordinal)
               || name.Contains(".json.corrupt-", StringComparison.Ordinal);
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

    internal static string AppVersion()
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

    private readonly record struct PreparedRestore(string TarGzPath, SelfBackupManifest Manifest, bool Encrypted, string? DecryptTemp,
        long ExtractedBytes);

    // R1-32: the start migrates the restored database and then reads it; one it cannot bring up to date (a history
    // behind its tables) or whose tables lack what the model maps would stop every start after the marker is
    // consumed. So the start's own migration runs first on a copy, with the app's migrations, then every mapped table
    // and column is read once; a failure refuses the restore. SQLite only: a PostgreSQL archive holds no database.
    // The probe logs nothing of its own: its messages would read as being about the live database.
    private async Task ProbeMigrationAsync(string stagedDb, CancellationToken ct)
    {
        if (!File.Exists(stagedDb)) return;
        var probeDir = Path.Combine(_dataPaths.SelfBackupsDir, ".build", "probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDir);
        var probe = Path.Combine(probeDir, SelfBackupFormat.DbEntryName);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = probe, Pooling = false }.ToString();
        try
        {
            File.Copy(stagedDb, probe);
            await using (var metrics = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite(connection, sql => sql.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)).Options))
            {
                await DatabaseInitializer.InitializeAsync(metrics, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);
                await ReadEveryMappedColumnAsync(metrics, ct);
            }
            await using (var identity = new WhiskersIdentityDbContext(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
                .UseSqlite(connection, sql =>
                {
                    sql.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly);
                    sql.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
                }).Options))
            {
                await identity.Database.MigrateAsync(ct);
                await ReadEveryMappedColumnAsync(identity, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Restore refused: the archive's database cannot be brought up to this version's schema");
            throw new BackupValidationException(
                "Backup archive is invalid: its database cannot be brought up to this version's schema (its migration history does not match its tables).");
        }
        finally
        {
            TryDeleteDir(probeDir);
        }
    }

    // R1-33: a file pg_restore cannot list, or a dump without Whiskers' tables in public, is no dump of this database.
    private async Task CheckPostgresDumpReadableAsync(string stagedDump, CancellationToken ct)
    {
        try
        {
            await _pgTools.CheckReadableAsync(stagedDump, ct);
        }
        catch (Exception ex) when (ex is PostgresToolException or InvalidDataException)
        {
            _logger.LogWarning("Restore refused: the database dump cannot be read ({Reason})", ex.Message);
            throw new BackupValidationException($"Backup archive's database cannot be restored into this server: {ex.Message}");
        }
    }

    // R1-33: the dump is applied to this server in a transaction that is rolled back — a dump this server
    // does not accept (a newer format, a broken file, missing rights to replace the schema) is refused here, before
    // the restore is committed, not at the start. Inside that transaction, as for SQLite (R1-32): the migration
    // histories must be the manifest's, the start's migrations must run on them, and every mapped table and column
    // must then be readable. The database is locked while the dry run holds the schema.
    private async Task ProbePostgresRestoreAsync(string stagedDump, SelfBackupManifest manifest, string safetyBackupId,
        bool forward, CancellationToken ct)
    {
        string connection;
        var probe = new System.Text.StringBuilder();
        using (var scope = _scopeFactory.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            connection = metrics.Database.GetConnectionString()
                         ?? throw new BackupValidationException("The PostgreSQL connection string is not known.");
            AppendPostgresProbe(probe, metrics, manifest.MetricsMigrations,
                Microsoft.EntityFrameworkCore.Migrations.HistoryRepository.DefaultTableName, forward);
            AppendPostgresProbe(probe, identity, manifest.IdentityMigrations, DatabaseRegistration.IdentityMigrationsHistoryTable, forward);
        }
        try
        {
            await _pgTools.DryRunAsync(connection, stagedDump, probe.ToString(), ct);
        }
        catch (Exception ex) when (ex is PostgresToolException or InvalidDataException)
        {
            _logger.LogWarning("Restore refused: the database dump cannot be restored into this server ({Reason})", ex.Message);
            // The dry run ran the archive's SQL: a script that committed part of it on its own may have changed the
            // database, whatever the error that ended it. The safety backup taken just before holds the state from before.
            var early = ex.Message.Contains("ended its transaction early", StringComparison.Ordinal);
            throw new BackupValidationException(
                (early
                    ? "Backup archive's database script ended the dry run's transaction on its own and may have changed the database. "
                    : $"Backup archive's database cannot be restored into this server: {ex.Message}. ") +
                $"The pre-restore safety backup {safetyBackupId} holds the database from before the dry run; if the database " +
                "was changed, recover from it as the backup guide describes (docs/operations/backup-and-recovery.md, " +
                "\"After a restore refused for an early commit\").");
        }
    }

    // The history check, the start's migrations (idempotent: each runs only when its history row is missing, as
    // MigrateAsync decides) and the mapped-column reads for one context. Migration ids are this build's own (the manifest
    // is a subset of them, CheckSubset); names come from the app's model; all of it quoted.
    private static void AppendPostgresProbe(System.Text.StringBuilder probe, DbContext db, IEnumerable<string> applied,
        string historyTable, bool forward)
    {
        static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
        var expected = applied.Order(StringComparer.Ordinal).Select(Literal);
        probe.Append("DO $probe$ BEGIN IF (SELECT coalesce(array_agg(\"MigrationId\"::text ORDER BY \"MigrationId\"::text COLLATE \"C\"), ARRAY[]::text[]) FROM ")
            .Append(QuoteIdentifier(historyTable))
            .Append(") IS DISTINCT FROM ARRAY[").Append(string.Join(", ", expected)).Append("]::text[] THEN RAISE EXCEPTION ")
            .Append(Literal($"the migration history in {historyTable} does not match the archive's manifest"))
            .AppendLine("; END IF; END $probe$;");
        // R1-34: a restore for the previous release checks the history only; this release's schema is not its own.
        if (!forward) return;
        // NoTransactions: the dry run's own transaction holds it all. A future migration with DDL that cannot run in a
        // transaction (CREATE INDEX CONCURRENTLY, ALTER TYPE … ADD VALUE) would fail this probe for every older archive.
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        probe.AppendLine(migrator.GenerateScript(options:
            Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.Idempotent
            | Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.NoTransactions));
        foreach (var query in MappedColumnQueries(db)) probe.Append(query).AppendLine(";");
    }

    // SELECT of every column the model maps, per table, with no rows: fails on a table or column that is missing.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The text is built only from the app's own EF model (quoted identifiers), see MappedColumnQueries.")]
    private static async Task ReadEveryMappedColumnAsync(DbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection();
        try
        {
            foreach (var query in MappedColumnQueries(db))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // Names from the app's own model, quoted: no input from the archive reaches this text.
    private static IEnumerable<string> MappedColumnQueries(DbContext db)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
            if (!tables.TryGetValue(table, out var columns)) tables[table] = columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in entity.GetProperties())
                if (property.GetColumnName(store) is { } column) columns.Add(column);
        }
        foreach (var (table, columns) in tables)
            if (columns.Count > 0)
                yield return $"SELECT {string.Join(", ", columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(table)} LIMIT 0";
    }

    // Staging takes the extracted bytes and the swap a second copy of each target beside the live one; the pre-restore
    // safety copy takes the live data twice at most (the database copy in the build dir, then the archive) (R1-32).
    private void EnsureFreeSpace(long extractedBytes)
    {
        long live = 0, available;
        try
        {
            foreach (var file in EnumerateDataFiles(Path.GetFullPath(_dataPaths.RootDir)))
                live += new FileInfo(file.SourcePath).Length;
            if (File.Exists(_dataPaths.DbPath)) live += new FileInfo(_dataPaths.DbPath).Length;
            available = new DriveInfo(Path.GetFullPath(_dataPaths.RootDir)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Free space of the data volume unknown; the restore goes ahead without the check");
            return;
        }
        var needed = extractedBytes * 3 + live * 2 + (64L << 20);   // the third copy: the SQLite migration probe (on PostgreSQL the probe runs in the server; the safety backup's pg_dump is not estimated here — on a full volume it fails, and nothing is changed)
        if (available < needed)
            throw new BackupValidationException(
                $"Not enough free space for this restore: it needs about {needed / (1 << 20)} MiB, the data volume has {available / (1 << 20)} MiB.");
    }
}
