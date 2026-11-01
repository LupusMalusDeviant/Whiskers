using Whiskers.Configuration;

namespace Whiskers.Services.Backup;

/// <summary>How a self-backup archive entry is treated (R1-32).</summary>
public enum BackupEntryKind
{
    /// <summary><c>manifest.json</c> at the archive root: read, never placed into the live data.</summary>
    Manifest,
    /// <summary>A known top-level data file (a JSON store, the setup flag, the SQLite database).</summary>
    File,
    /// <summary>A known data directory or anything below it.</summary>
    Directory,
    /// <summary>Not a restore target (unknown, or local to the install): left out of staging, never written.</summary>
    NotRestored,
    /// <summary>Never allowed in an archive (backups, restore state, live journals, temp files, a vault key):
    /// the whole archive is refused.</summary>
    Forbidden,
}

/// <summary>
/// The one list of what a self-backup holds and a restore may write (R1-32, INV-55). Packing takes only these
/// targets, the restore validator refuses forbidden entries and leaves unknown ones out, and the boot swap makes
/// every target equal to the archive: a target the archive does not hold is removed (the pre-restore safety backup
/// keeps it) — except the database (only replaced, an archive of a SQLite instance always holds it), the audit
/// checkpoint (kept aside, see <see cref="RestoreBootHandler"/>) and the audit outbox (merged: each file is an audit
/// event no database holds yet).
/// </summary>
public static class SelfBackupLayout
{
    private static readonly DataPathOptions Probe = new("/layout-probe");

    private static string Rel(string path) => path[(Probe.RootDir.Length + 1)..];

    /// <summary>Top-level data files that are restore targets.</summary>
    public static IReadOnlyList<string> Files { get; } =
    [
        Rel(Probe.DbPath),
        Rel(Probe.AgentSettingsJson),
        Rel(Probe.AppSettingsJson),
        Rel(Probe.McpPermissionsJson),
        Rel(Probe.ImageUpdatesJson),
        Rel(Probe.ServersJson),
        Rel(Probe.VaultJson),
        Rel(Probe.RolesJson),
        Rel(Probe.WhitelistJson),
        Rel(Probe.SetupCompleteFlag),
        Rel(Probe.NotificationPrefsJson),
        Rel(Probe.GuardrailsJson),
        Rel(Probe.CveFindingsJson),
        Rel(Probe.CertificateAlertsJson),
        Rel(Probe.AiTriggersJson),
        Rel(Probe.AutomationOwnersJson),
        Rel(Probe.AuditCheckpointJson),
        Rel(Probe.KnownLocalAccountsJson),
        "git-deploys.json",   // GitDeployService
        "registries.json",    // RegistryConfigService
    ];

    /// <summary>Top-level data directories that are restore targets (with everything below them).</summary>
    public static IReadOnlyList<string> Directories { get; } =
    [
        Rel(Probe.KeysDir),
        Rel(Probe.SshKeysDir),
        Rel(Probe.MtlsDir),
        Rel(Probe.ChatDir),
        Rel(Probe.AgentChatDir),
        Rel(Probe.AuditOutboxDir),
        Rel(Probe.TailscaleStateDir),
        Rel(Probe.NetbirdConfigPath).Split('/')[0],
    ];

    /// <summary>Data paths that belong to this install and are neither backed up nor restored, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> NotRestored { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Rel(Probe.LegacyApiKeysJson)] = "removed plaintext key store, deleted on start",
        [Rel(Probe.LegacyInitialMcpKeyFile)] = "removed plaintext key store, deleted on start",
        [Rel(Probe.SetupTokenFile)] = "one-time token of this install's first-run setup",
        [Rel(Probe.BackupsDir)] = "the backups themselves, incl. the pre-restore safety copy",
        [Rel(Probe.RestoreStagingDir)] = "restore state",
        [Rel(Probe.RestorePendingMarker)] = "restore state",
        [Rel(Probe.RestoreAppliedNote)] = "restore state",
    };

    /// <summary>The audit outbox: merged on restore instead of replaced.</summary>
    public static string AuditOutbox { get; } = Rel(Probe.AuditOutboxDir);

    /// <summary>The audit checkpoint: kept aside instead of removed when the archive has none.</summary>
    public static string AuditCheckpoint { get; } = Rel(Probe.AuditCheckpointJson);

    /// <summary>Directories holding private keys: restored with owner-only permissions.</summary>
    public static IReadOnlyList<string> SecretDirectories { get; } = [Rel(Probe.KeysDir), Rel(Probe.SshKeysDir), Rel(Probe.MtlsDir)];

    /// <summary>Files holding secrets: restored with owner-only permissions.</summary>
    public static IReadOnlyList<string> SecretFiles { get; } = [Rel(Probe.VaultJson)];

    private static readonly HashSet<string> FileSet = new(Files, StringComparer.Ordinal);
    private static readonly HashSet<string> DirectorySet = new(Directories, StringComparer.Ordinal);

    private static readonly string[] ForbiddenSuffixes = ["-wal", "-shm", "-journal", ".tmp", ".__restoring"];

    /// <summary>Normalises an entry name to forward slashes without a leading <c>./</c> or a trailing slash.</summary>
    public static string Normalize(string entryName)
    {
        var name = entryName.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        return name.TrimEnd('/');
    }

    /// <summary>Classifies one archive entry; <paramref name="isDirectory"/> is its tar entry type.</summary>
    public static BackupEntryKind Classify(string entryName, bool isDirectory)
    {
        var name = Normalize(entryName);
        if (name.Length == 0) return BackupEntryKind.Forbidden;
        var segments = name.Split('/');
        var top = segments[0];
        var last = segments[^1];

        if (string.Equals(top, Rel(Probe.BackupsDir), StringComparison.OrdinalIgnoreCase)
            || top.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(top, "vault.key", StringComparison.OrdinalIgnoreCase)
            || ForbiddenSuffixes.Any(s => last.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return BackupEntryKind.Forbidden;

        if (segments.Length == 1 && string.Equals(name, SelfBackupFormat.ManifestEntryName, StringComparison.Ordinal))
            return isDirectory ? BackupEntryKind.Forbidden : BackupEntryKind.Manifest;

        if (FileSet.Contains(top))
            // A known file must be a file at the root; a directory in its place, or anything below it, is not ours.
            return segments.Length == 1 && !isDirectory ? BackupEntryKind.File : BackupEntryKind.Forbidden;

        if (DirectorySet.Contains(top))
            // A file where a known directory belongs would replace the directory with it.
            return segments.Length == 1 && !isDirectory ? BackupEntryKind.Forbidden : BackupEntryKind.Directory;

        return BackupEntryKind.NotRestored;
    }
}
