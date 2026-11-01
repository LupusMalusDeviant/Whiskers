using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Whiskers.Models;

namespace Whiskers.Services.Backup;

/// <summary>
/// Checks the extracted contents of a restore BEFORE the commit marker is written (R1-32, INV-55): a staged SQLite
/// database must pass <c>PRAGMA integrity_check</c> and hold exactly the migrations its manifest names, every staged
/// JSON store must read as the type its service loads at start (a store that only parses would still stop the start),
/// and every staged data-protection key must be well-formed XML. A failure throws
/// <see cref="BackupValidationException"/> and nothing is committed — the live data stays as it is.
/// </summary>
public static class StagedRestoreValidator
{
    // The stores whose service reads them at start without catching a malformed file, with the type it reads; the
    // options are those of JsonFileStore. The two configuration layers must hold an object (AddJsonFile).
    private static readonly IReadOnlyDictionary<string, Type> StoreTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["roles.json"] = typeof(UserRoleData),
        ["whitelist.json"] = typeof(WhitelistData),
        ["vault.json"] = typeof(VaultData),
        ["servers.json"] = typeof(ServerConfigData),
        ["mcp-permissions.json"] = typeof(McpPermissionData),
        ["guardrails.json"] = typeof(Whiskers.Configuration.GuardrailConfig),
        ["notification-prefs.json"] = typeof(ContainerNotificationPrefs),
        ["ai-triggers.json"] = typeof(Whiskers.Services.Agent.Triggers.AiTriggerData),
        ["git-deploys.json"] = typeof(GitDeployData),
        ["registries.json"] = typeof(RegistryConfigData),
    };

    private static readonly HashSet<string> ObjectRoots = new(StringComparer.Ordinal) { "app-settings.json", "agent-settings.json" };

    private static readonly JsonSerializerOptions StoreOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The stores checked by type or root kind (a test holds them to the restore targets).</summary>
    public static IEnumerable<string> TypedStores => StoreTypes.Keys.Concat(ObjectRoots);

    public static void Validate(string stagingDir, SelfBackupManifest manifest)
    {
        var db = Path.Combine(stagingDir, SelfBackupFormat.DbEntryName);
        if (File.Exists(db))
            ValidateDatabase(db, manifest);

        foreach (var file in SelfBackupLayout.Files.Where(f => f.EndsWith(".json", StringComparison.Ordinal)))
        {
            var path = Path.Combine(stagingDir, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                if (StoreTypes.TryGetValue(file, out var type))
                    JsonSerializer.Deserialize(stream, type, StoreOptions);
                else
                {
                    using var document = JsonDocument.Parse(stream);
                    if (ObjectRoots.Contains(file) && document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("not an object");
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new BackupValidationException($"Backup archive is invalid: {file} is not a valid store.");
            }
        }

        var keys = Path.Combine(stagingDir, "keys");
        if (Directory.Exists(keys))
            foreach (var key in Directory.EnumerateFiles(keys, "*.xml"))
            {
                try { XDocument.Load(key); }
                catch (XmlException)
                {
                    throw new BackupValidationException($"Backup archive is invalid: keys/{Path.GetFileName(key)} is not valid XML.");
                }
            }
    }

    private static void ValidateDatabase(string path, SelfBackupManifest manifest)
    {
        // Read-only and unpooled: a pooled handle would keep the staged file open on Windows and block the swap.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA integrity_check";
                using var reader = check.ExecuteReader();
                var results = new List<string>();
                while (reader.Read()) results.Add(reader.GetString(0));
                if (results is not ["ok"])
                    throw new BackupValidationException(
                        $"Backup archive is invalid: its database fails the integrity check ({results.FirstOrDefault() ?? "no result"}).");
            }

            CheckHistory(connection, "__EFMigrationsHistory", manifest.MetricsMigrations, "metrics");
            CheckHistory(connection, Whiskers.Services.Persistence.DatabaseRegistration.IdentityMigrationsHistoryTable,
                manifest.IdentityMigrations, "identity");
            CheckStartCanMigrate(connection, manifest);
        }
        catch (SqliteException ex)
        {
            throw new BackupValidationException($"Backup archive is invalid: its database cannot be read ({ex.SqliteErrorCode}).");
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
            throw new BackupValidationException("Backup archive is invalid: its migration history cannot be read.");
        }
    }

    // Without a history the start migrates from the beginning, and a table already there stops it — every start, after
    // the marker is gone. The start baselines only a metrics schema it recognises (DatabaseInitializer: the
    // ContainerMetrics table); identity has no baseline. Such a database is refused here instead.
    private static void CheckStartCanMigrate(SqliteConnection connection, SelfBackupManifest manifest)
    {
        // No migration creates a trigger or a view; one in an archive would run only on the live database.
        using (var extra = connection.CreateCommand())
        {
            extra.CommandText = "SELECT count(*) FROM sqlite_master WHERE type IN ('trigger', 'view')";
            if (Convert.ToInt64(extra.ExecuteScalar()) > 0)
                throw new BackupValidationException("Backup archive is invalid: its database holds triggers or views no version of Whiskers creates.");
        }
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name NOT LIKE '\\_\\_%MigrationsHistory' ESCAPE '\\'";
            using var reader = list.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var identityTables = tables.Where(t => t.StartsWith("AspNet", StringComparison.Ordinal)).ToList();
        if (manifest.IdentityMigrations.Length == 0 && identityTables.Count > 0)
            throw new BackupValidationException(
                "Backup archive is invalid: its database holds sign-in tables without their migration history.");
        if (manifest.MetricsMigrations.Length == 0 && tables.Except(identityTables).Any()
            && !tables.Contains(Whiskers.Services.Persistence.DatabaseInitializer.SentinelTable))
            throw new BackupValidationException(
                "Backup archive is invalid: its database holds tables without their migration history.");
    }

    // The manifest's migration lists drive the schema check before the restore; the database must say the same, or
    // the check was made against something other than what would be restored.
    private static void CheckHistory(SqliteConnection connection, string table, string[] expected, string which)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            exists.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
            {
                using var read = connection.CreateCommand();
                read.CommandText = $"SELECT \"MigrationId\" FROM \"{table}\"";   // table name is one of two constants
                using var reader = read.ExecuteReader();
                while (reader.Read()) applied.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");
            }
        }
        if (!applied.SetEquals(expected))
            throw new BackupValidationException(
                $"Backup archive is invalid: the {which} migrations in its database do not match its manifest.");
    }
}
