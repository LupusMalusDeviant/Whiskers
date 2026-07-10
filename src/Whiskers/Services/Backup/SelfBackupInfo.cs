namespace Whiskers.Services.Backup;

/// <summary>
/// Listing/UI view of a self-backup, and the shape of the plaintext <c>&lt;archive&gt;.meta.json</c>
/// sidecar stored next to each archive so the backup list renders without decrypting the body.
/// The archive-internal <see cref="SelfBackupManifest"/> remains authoritative at restore time.
/// </summary>
public sealed class SelfBackupInfo
{
    /// <summary>12-char backup id (also the archive filename stem).</summary>
    public string Id { get; set; } = "";

    /// <summary>Archive filename within <c>backups/self</c>.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Archive size in bytes (re-stat'd at list time).</summary>
    public long SizeBytes { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Optional human/scheduled label.</summary>
    public string Label { get; set; } = "";

    /// <summary>Database provider at backup time ("sqlite"/"postgres").</summary>
    public string Provider { get; set; } = "";

    /// <summary>True when the archive body is AES-256-GCM encrypted (VAULT_KEY was set at backup time).</summary>
    public bool Encrypted { get; set; }

    /// <summary>Backup archive format version.</summary>
    public int FormatVersion { get; set; }
}
