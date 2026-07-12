using System.Diagnostics;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Database;

public class DatabaseService : IDatabaseService
{
    private readonly IHostCommandExecutor _hostExec;
    private readonly ILogger<DatabaseService> _logger;

    // Host-side directory the dumps are copied to. Same location volume backups use, so both survive a
    // container recreate (the DB container's own /tmp does not).
    private readonly string _hostBackupDir;

    public DatabaseService(IHostCommandExecutor hostExec, ILogger<DatabaseService> logger, DataPathOptions? dataPaths = null)
    {
        _hostExec = hostExec;
        _logger = logger;
        _hostBackupDir = (dataPaths ?? DataPathOptions.Default).BackupsDir;
    }

    // Run a command in a container via `docker exec` on the HOST shell plane (nsenter / SSH) instead
    // of the Docker API's exec endpoint. The mTLS socket-proxy blocks container exec (EXEC=0), but the
    // host's own docker.sock has full access — so this works uniformly on Local, SSH and TCP+mTLS.
    private async Task<(string StdOut, string StdErr, int ExitCode)> ExecInContainer(
        string containerId, string[] cmd, string? serverId = null, TimeSpan? timeout = null)
    {
        var dockerCmd = "docker exec " + ShellUtils.Quote(containerId) + " " +
                        string.Join(' ', cmd.Select(ShellUtils.Quote));
        var r = await _hostExec.ExecuteAsync(serverId ?? "local", dockerCmd, timeout ?? TimeSpan.FromSeconds(30));
        return (r.Output, r.Error, r.ExitCode);
    }

    public async Task<QueryResult> ExecuteQueryAsync(string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        var sw = Stopwatch.StartNew();
        var cmd = BuildQueryCommand(dbType, creds, query);
        var (stdout, stderr, exitCode) = await ExecInContainer(containerId, cmd, serverId, TimeSpan.FromSeconds(30));
        sw.Stop();

        if (exitCode != 0)
            return new QueryResult { Error = stderr.Trim(), DurationMs = sw.Elapsed.TotalMilliseconds };

        return ParseQueryResult(stdout, dbType, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<List<string>> GetDatabasesAsync(string containerId, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-t", "-A", "-c", "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname" },
            DatabaseType.MySQL => BuildMysqlCmd(creds, "-e", "SHOW DATABASES"),
            DatabaseType.MongoDB => new[] { "mongosh", "--quiet", "--eval", "db.adminCommand('listDatabases').databases.forEach(d => print(d.name))" },
            DatabaseType.Redis => new[] { "redis-cli", "CONFIG", "GET", "databases" },
            DatabaseType.Neo4j => new[] { "cypher-shell", "-u", creds.User, "-p", creds.Password, "SHOW DATABASES YIELD name RETURN name" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0) return new();

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId);
        if (exitCode != 0) return new();

        // Redis has no named databases — only numbered logical DBs. Translate the configured count into
        // indices instead of letting the generic parser return the count itself as a "database name".
        if (dbType == DatabaseType.Redis)
            return ParseRedisDatabaseList(stdout);

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("+-") && !l.StartsWith("Database") && !l.StartsWith("databases"))
            .Select(l => l.Trim('|', ' '))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    /// <summary>Turns the output of <c>redis-cli CONFIG GET databases</c> (the key name plus the configured
    /// count, e.g. "databases\n16") into the list of numbered logical databases ("0".."N-1"). Redis addresses
    /// databases by index via <c>SELECT n</c>, not by name. Falls back to a single db0 when the count can't be
    /// parsed — Redis always has at least database 0.</summary>
    public static List<string> ParseRedisDatabaseList(string stdout)
    {
        var count = (stdout ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim('|', ' ', '"', '\t'))
            .Where(l => int.TryParse(l, out _))
            .Select(int.Parse)
            .DefaultIfEmpty(0)
            .Last();

        if (count <= 0) count = 1; // at least db0
        return Enumerable.Range(0, count).Select(i => i.ToString()).ToList();
    }

    public async Task<List<TableInfo>> GetTablesAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-d", database, "-t", "-A", "-c",
                "SELECT t.tablename, pg_size_pretty(pg_total_relation_size('public.' || t.tablename)), COALESCE(s.n_live_tup, 0) FROM pg_tables t LEFT JOIN pg_stat_user_tables s ON t.tablename = s.relname WHERE t.schemaname = 'public' ORDER BY t.tablename" },
            DatabaseType.MySQL => BuildMysqlCmd(creds, "-D", database, "-e", "SELECT TABLE_NAME, TABLE_ROWS, ROUND(((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024), 2) AS 'Size_MB' FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME"),
            DatabaseType.MongoDB => new[] { "mongosh", "--quiet", database, "--eval", "db.getCollectionNames().forEach(c => { var s = db[c].stats(); print(c + '|' + s.count + '|' + s.size) })" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0) return new();

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId);
        if (exitCode != 0) return new();

        var tables = new List<TableInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("+-") || line.StartsWith("TABLE") || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|');
            if (parts.Length >= 1)
            {
                tables.Add(new TableInfo
                {
                    Name = parts[0].Trim(),
                    RowCount = parts.Length > 1 && long.TryParse(parts[1].Trim(), out var rc) ? rc : 0,
                    Size = parts.Length > 2 ? parts[2].Trim() : ""
                });
            }
        }
        return tables;
    }

    public async Task<List<ColumnInfo>> GetSchemaAsync(string containerId, string database, string table, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        // Escape the table identifier before embedding it in the query strings: '' for the
        // SQL string literals (psql) and `` for the backtick-quoted identifier (MySQL).
        var pgTable = table.Replace("'", "''");
        var myTable = table.Replace("`", "``");
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-d", database, "-t", "-A", "-c",
                $"SELECT c.column_name, c.data_type, c.is_nullable, c.column_default, CASE WHEN pk.column_name IS NOT NULL THEN 'YES' ELSE 'NO' END FROM information_schema.columns c LEFT JOIN (SELECT kcu.column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_name = '{pgTable}') pk ON c.column_name = pk.column_name WHERE c.table_name = '{pgTable}' AND c.table_schema = 'public' ORDER BY c.ordinal_position" },
            DatabaseType.MySQL => BuildMysqlCmd(creds, "-D", database, "-e", $"DESCRIBE `{myTable}`"),
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0) return new();

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId);
        if (exitCode != 0) return new();

        var columns = new List<ColumnInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("+-") || line.StartsWith("Field") || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length >= 2)
            {
                columns.Add(new ColumnInfo
                {
                    Name = parts[0],
                    Type = parts[1],
                    Nullable = parts.Length > 2 && parts[2].Contains("YES", StringComparison.OrdinalIgnoreCase),
                    Default = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null,
                    PrimaryKey = parts.Length > 4 && parts[4]?.Contains("YES", StringComparison.OrdinalIgnoreCase) == true
                });
            }
        }
        return columns;
    }

    public async Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{database}_{timestamp}.sql";
        var backupPath = $"/tmp/{fileName}";

        // Interpolated values run through `sh -c`, so every one is POSIX-single-quoted (Sq) to prevent
        // shell breakage/injection from passwords or DB names containing spaces or metacharacters.
        // Passwords are passed via env vars (PGPASSWORD/MYSQL_PWD) instead of argv so they never appear
        // in the container's process list.
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "sh", "-c", $"PGPASSWORD={Sq(creds.Password)} pg_dump -U {Sq(creds.User)} {Sq(database)} > {Sq(backupPath)}" },
            DatabaseType.MySQL => new[] { "sh", "-c", $"MYSQL_PWD={Sq(creds.Password)} mysqldump -u {Sq(creds.User)} {Sq(database)} > {Sq(backupPath)}" },
            DatabaseType.MongoDB => new[] { "sh", "-c", $"mongodump --db {Sq(database)} --archive={Sq(backupPath + ".gz")} --gzip" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0)
            return (false, "", 0, $"Backup is not supported for {dbType}.");

        var (stdout, stderr, exitCode) = await ExecInContainer(containerId, cmd, serverId, TimeSpan.FromMinutes(5));

        if (exitCode != 0)
            return (false, "", 0, stderr.Trim());

        // The dump currently lives in the container's /tmp, which is lost on the next container recreate
        // (image update, redeploy). Copy it out to the host backup dir and delete the in-container copy so
        // the backup is actually durable. The file name is derived from a timestamp + DB name; quote it.
        var containerFile = dbType == DatabaseType.MongoDB ? $"{backupPath}.gz" : backupPath;
        var hostFileName = dbType == DatabaseType.MongoDB ? $"{fileName}.gz" : fileName;
        var hostPath = $"{_hostBackupDir}/{hostFileName}";
        var sid = serverId ?? "local";

        await _hostExec.ExecuteAsync(sid, $"mkdir -p {ShellUtils.Quote(_hostBackupDir)}", TimeSpan.FromSeconds(5));
        var cp = await _hostExec.ExecuteAsync(sid,
            $"docker cp {ShellUtils.Quote(containerId + ":" + containerFile)} {ShellUtils.Quote(hostPath)} 2>&1",
            TimeSpan.FromMinutes(5));
        // Best-effort cleanup of the in-container temp file regardless of cp outcome.
        await ExecInContainer(containerId, new[] { "rm", "-f", containerFile }, serverId);

        if (!cp.Success)
            return (false, "", 0, $"Dump created, but copying it to the host failed: {cp.Output} {cp.Error}".Trim());

        var sizeResult = await _hostExec.ExecuteAsync(sid, $"stat -c %s {ShellUtils.Quote(hostPath)}", TimeSpan.FromSeconds(5));
        long.TryParse(sizeResult.Output.Trim(), out var size);

        return (true, hostPath, size, "");
    }

    private static string[] BuildQueryCommand(DatabaseType dbType, DatabaseCredentials creds, string query) => dbType switch
    {
        DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-d", creds.Database, "-c", query },
        DatabaseType.MySQL => BuildMysqlCmd(creds, "-D", creds.Database, "-e", query),
        DatabaseType.MongoDB => new[] { "mongosh", "--quiet", creds.Database, "--eval", query },
        DatabaseType.Redis => new[] { "redis-cli" }.Concat(query.Split(' ')).ToArray(),
        DatabaseType.Neo4j => new[] { "sh", "-c", $"NEO4J_PASSWORD={Sq(creds.Password)} cypher-shell -u {Sq(creds.User)} {Sq(query)}" },
        _ => new[] { "echo", "Unsupported database type" }
    };

    /// <summary>POSIX single-quote escaping for values interpolated into a <c>sh -c</c> string.</summary>
    private static string Sq(string? s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    private static string[] BuildMysqlCmd(DatabaseCredentials creds, params string[] extraArgs)
    {
        // Pass the password via MYSQL_PWD (env), never -p<pw> in argv, so it can't appear in the container's
        // process list. The whole invocation runs through `sh -c`; every value is Sq-quoted (mirrors
        // BackupDatabaseAsync).
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(creds.Password))
            sb.Append($"MYSQL_PWD={Sq(creds.Password)} ");
        sb.Append($"mysql -u {Sq(creds.User)}");
        foreach (var arg in extraArgs)
            sb.Append(' ').Append(Sq(arg));
        return new[] { "sh", "-c", sb.ToString() };
    }

    private static QueryResult ParseQueryResult(string output, DatabaseType dbType, double durationMs)
    {
        var result = new QueryResult { DurationMs = durationMs };
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            result.RowCount = 0;
            return result;
        }

        // PostgreSQL with -c outputs a table with headers
        // MySQL outputs tab-separated with headers
        // MongoDB outputs JSON
        // Redis outputs plain text
        if (dbType == DatabaseType.Redis || dbType == DatabaseType.MongoDB)
        {
            // Plain text output — single column
            result.Columns.Add("Result");
            foreach (var line in lines)
                result.Rows.Add(new List<string> { line });
            result.RowCount = result.Rows.Count;
            return result;
        }

        // Tab/pipe-separated table output
        bool headerParsed = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip separator lines (postgres: ----+----, mysql: +----+)
            if (trimmed.StartsWith("--") || trimmed.StartsWith("+-") || trimmed.StartsWith("(") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Split by | (postgres) or \t (mysql)
            var cells = trimmed.Contains('|')
                ? trimmed.Split('|').Select(c => c.Trim()).ToList()
                : trimmed.Split('\t').Select(c => c.Trim()).ToList();

            if (!headerParsed)
            {
                result.Columns = cells;
                headerParsed = true;
            }
            else
            {
                result.Rows.Add(cells);
            }
        }

        result.RowCount = result.Rows.Count;
        return result;
    }
}
