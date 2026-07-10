namespace Whiskers.Services.Backup;

/// <summary>
/// Whiskers self-backup and restore of the application's OWN <c>/app/data</c> (roadmap F3). This is a CORE
/// service (always registered, no module gate), so the scheduler and the Settings UI can depend on it directly.
/// Backups are consistent tar.gz snapshots (SQLite folded via VACUUM), optionally AES-256-GCM encrypted with a
/// key derived from <c>VAULT_KEY</c>. Restore is a crash-safe deferred swap: the validated contents are staged
/// and the process is stopped, and a boot-time handler performs the file swap in the fresh process.
/// </summary>
public interface IBackupService
{
    /// <summary>Creates a consistent snapshot of <c>/app/data</c> (encrypted when VAULT_KEY is set) and returns
    /// its listing info.</summary>
    Task<SelfBackupInfo> CreateBackupAsync(string label, CancellationToken ct = default);

    /// <summary>Lists existing self-backups, newest first, from the plaintext sidecars (no decryption).</summary>
    Task<IReadOnlyList<SelfBackupInfo>> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>Resolves the on-disk archive path for a backup id (validated + contained), or null if absent.</summary>
    string? ResolveArchivePath(string id);

    /// <summary>Deletes a backup archive and its sidecar.</summary>
    Task DeleteBackupAsync(string id, CancellationToken ct = default);

    /// <summary>Retention: keeps the newest <paramref name="keepNewest"/> backups and deletes the rest.</summary>
    Task PruneAsync(int keepNewest, CancellationToken ct = default);

    /// <summary>Streams an uploaded archive to a private temp file under the backups directory and returns its
    /// path, for a subsequent <see cref="ValidateArchiveAsync"/> / <see cref="BeginRestoreAsync"/>. Keeps the
    /// path logic (and the data directory) inside the service so the UI needs no filesystem access.</summary>
    Task<string> StageUploadAsync(Stream source, CancellationToken ct = default);

    /// <summary>Discards an upload staged by <see cref="StageUploadAsync"/> (e.g. the user cancelled before a
    /// restore started). Idempotent and safe: only deletes an upload temp this service created.</summary>
    Task DiscardUploadAsync(string uploadPath, CancellationToken ct = default);

    /// <summary>Validates an uploaded archive WITHOUT touching live state (decrypts with the current VAULT_KEY
    /// if needed, checks the manifest/provider/schema compatibility and enforces the extraction guards).</summary>
    Task<BackupValidationResult> ValidateArchiveAsync(string uploadedPath, CancellationToken ct = default);

    /// <summary>Restores from a validated uploaded archive: enters maintenance, takes a pre-restore safety
    /// backup, stages the decrypted contents, writes the commit marker and stops the process so the boot
    /// handler performs the swap on restart. Throws on validation failure or if the safety backup fails.</summary>
    Task BeginRestoreAsync(string uploadedPath, string actor, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IBackupService.ValidateArchiveAsync"/>.</summary>
public sealed class BackupValidationResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public SelfBackupManifest? Manifest { get; init; }
    public bool Encrypted { get; init; }

    public static BackupValidationResult Fail(string error) => new() { Ok = false, Error = error };
    public static BackupValidationResult Success(SelfBackupManifest manifest, bool encrypted)
        => new() { Ok = true, Manifest = manifest, Encrypted = encrypted };
}

/// <summary>Raised for a user-actionable validation failure (bad manifest, wrong VAULT_KEY, incompatible
/// schema/provider). Its message is safe to surface directly in the UI.</summary>
public sealed class BackupValidationException : Exception
{
    public BackupValidationException(string message) : base(message) { }
}
