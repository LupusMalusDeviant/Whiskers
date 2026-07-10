namespace Whiskers.Services.Backup;

/// <summary>
/// Authoritative metadata written as <c>manifest.json</c> at the root of every self-backup archive.
/// It travels inside the (possibly encrypted) tar.gz and is the source of truth for restore-time
/// compatibility checks (schema/provider). A plaintext <see cref="SelfBackupInfo"/> sidecar mirrors
/// the non-secret subset next to the archive so the backup list renders without decrypting anything.
/// </summary>
public sealed class SelfBackupManifest
{
    /// <summary>Backup archive format version; bumped only on a breaking layout/crypto change.</summary>
    public int FormatVersion { get; set; } = SelfBackupFormat.CurrentVersion;

    /// <summary>Informational app/assembly version that produced the backup.</summary>
    public string AppVersion { get; set; } = "";

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Optional human/scheduled label (e.g. "manual", "scheduled-nightly", "pre-restore-...").</summary>
    public string Label { get; set; } = "";

    /// <summary>Database provider at backup time: "sqlite" or "postgres". A postgres backup captures no
    /// relational data (that DB is external) — only the config/secrets under /app/data.</summary>
    public string Provider { get; set; } = "";

    /// <summary>EF migrations applied to MetricsDbContext at backup time (restore subset-check input).</summary>
    public string[] MetricsMigrations { get; set; } = Array.Empty<string>();

    /// <summary>EF migrations applied to WhiskersIdentityDbContext at backup time (separate history table).</summary>
    public string[] IdentityMigrations { get; set; } = Array.Empty<string>();
}

/// <summary>Self-backup archive format constants, shared by the writer, reader and restore validator.</summary>
public static class SelfBackupFormat
{
    /// <summary>Current backup archive format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The manifest entry name at the archive root.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>The staged/canonical SQLite database entry name inside the archive (WAL already folded in).</summary>
    public const string DbEntryName = "metrics.db";

    /// <summary>Provider token for a SQLite-backed instance.</summary>
    public const string ProviderSqlite = "sqlite";

    /// <summary>Provider token for a PostgreSQL-backed instance.</summary>
    public const string ProviderPostgres = "postgres";
}
