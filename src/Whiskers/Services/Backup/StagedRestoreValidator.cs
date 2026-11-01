using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Whiskers.Services.Backup;

/// <summary>
/// Checks the extracted contents of a restore BEFORE the commit marker is written (R1-32, INV-55): a staged SQLite
/// database must pass <c>PRAGMA integrity_check</c> and hold exactly the migrations its manifest names, every staged
/// JSON store must parse, and every staged data-protection key must be well-formed XML. A failure throws
/// <see cref="BackupValidationException"/> and nothing is committed — the live data stays as it is.
/// </summary>
public static class StagedRestoreValidator
{
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
                using var _ = JsonDocument.Parse(stream);
            }
            catch (JsonException)
            {
                throw new BackupValidationException($"Backup archive is invalid: {file} is not valid JSON.");
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
            CheckHistory(connection, "__IdentityMigrationsHistory", manifest.IdentityMigrations, "identity");
        }
        catch (SqliteException ex)
        {
            throw new BackupValidationException($"Backup archive is invalid: its database cannot be read ({ex.SqliteErrorCode}).");
        }
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
                while (reader.Read()) applied.Add(reader.GetString(0));
            }
        }
        if (!applied.SetEquals(expected))
            throw new BackupValidationException(
                $"Backup archive is invalid: the {which} migrations in its database do not match its manifest.");
    }
}
