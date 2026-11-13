using System.Diagnostics;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;
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
    // CodeQL #23–#26: the password of the database never stands in the command. The client reads it from its environment
    // (PasswordVariable), which `docker exec -e NAME` fills from the host shell's environment, which the executor fills
    // beside the command (HostCommandSecrets) — so the command that is logged, audited or shown in a process list on the
    // host or in the container names the variable only, whatever the password looks like.
    private async Task<(string StdOut, string StdErr, int ExitCode)> ExecInContainer(
        string containerId, string[] cmd, string? serverId = null, TimeSpan? timeout = null,
        DatabaseType dbType = DatabaseType.None, DatabaseCredentials? creds = null, CancellationToken ct = default)
    {
        var secrets = PasswordVariable(dbType) is { } name && !string.IsNullOrEmpty(creds?.Password)
            ? [new KeyValuePair<string, string>(name, creds.Password)]
            : Array.Empty<KeyValuePair<string, string>>();
        // The container is an operand of docker (R1-42): checked, and after "--" so it is never read as an option.
        var dockerCmd = "docker exec " + string.Concat(secrets.Select(s => $"-e {s.Key} ")) + "-- " +
                        ShellUtils.Quote(CommandOperands.RequireContainerRef(containerId)) + " " +
                        string.Join(' ', cmd.Select(ShellUtils.Quote));
        using var delivered = HostCommandSecrets.Provide(secrets);
        var r = await _hostExec.ExecuteAsync(OmittedServer.For<IDatabaseService>(serverId), dockerCmd, timeout ?? TimeSpan.FromSeconds(30), ct);
        return (r.Output, r.Error, r.ExitCode);
    }

    /// <summary>The environment variable the client of <paramref name="dbType"/> reads its password from: PostgreSQL
    /// <c>PGPASSWORD</c> (psql, pg_dump), MySQL/MariaDB <c>MYSQL_PWD</c> (mysql, mysqldump), Redis <c>REDISCLI_AUTH</c>,
    /// Neo4j <c>NEO4J_PASSWORD</c> (cypher-shell). MongoDB: none — mongosh reads a password only from argv, a URI or a
    /// prompt, and Whiskers does not authenticate to MongoDB (it never passed the password), so nothing is sent.</summary>
    internal static string? PasswordVariable(DatabaseType dbType) => dbType switch
    {
        DatabaseType.PostgreSQL => "PGPASSWORD",
        DatabaseType.MySQL => "MYSQL_PWD",
        DatabaseType.Redis => "REDISCLI_AUTH",
        DatabaseType.Neo4j => "NEO4J_PASSWORD",
        _ => null,
    };

    public async Task<QueryResult> ExecuteQueryAsync(string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
    {
        Withhold(creds);
        var sw = Stopwatch.StartNew();
        // INV-19: the database the query runs in is an operand of the client (psql -d, mysql -D, mongosh's
        // positional database); an empty one keeps the client's default.
        if (!string.IsNullOrEmpty(creds.Database) && dbType is DatabaseType.PostgreSQL or DatabaseType.MySQL or DatabaseType.MongoDB)
            CheckDatabaseName(creds.Database, dbType);
        var cmd = BuildQueryCommand(dbType, creds, query);
        var (stdout, stderr, exitCode) = await ExecInContainer(containerId, cmd, serverId, TimeSpan.FromSeconds(30), dbType, creds);
        sw.Stop();

        if (exitCode != 0)
            return new QueryResult { Error = stderr.Trim(), DurationMs = sw.Elapsed.TotalMilliseconds };

        return ParseQueryResult(stdout, dbType, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<List<string>> GetDatabasesAsync(string containerId, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null,
        CancellationToken ct = default)
    {
        Withhold(creds);
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-t", "-A", "-c", "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname" },
            DatabaseType.MySQL => BuildMysqlCmd(creds, "-e", "SHOW DATABASES"),
            DatabaseType.MongoDB => new[] { "mongosh", "--quiet", "--eval", "db.adminCommand('listDatabases').databases.forEach(d => print(d.name))" },
            DatabaseType.Redis => new[] { "redis-cli", "CONFIG", "GET", "databases" },
            // The password from NEO4J_PASSWORD (ExecInContainer), never "-p <password>" in argv.
            DatabaseType.Neo4j => new[] { "cypher-shell", "-u", creds.User, "SHOW DATABASES YIELD name RETURN name" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0) return new();

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId, dbType: dbType, creds: creds, ct: ct);
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

    public async Task<List<TableInfo>> GetTablesAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null,
        CancellationToken ct = default)
    {
        Withhold(creds);
        // INV-19: a read listing must not become arbitrary execution — the name is an operand of the client
        // (mongosh would read "--eval=…" as an option and run it), so it is checked before it reaches one.
        CheckDatabaseName(database, dbType);
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "psql", "-U", creds.User, "-d", database, "-t", "-A", "-c",
                "SELECT t.tablename, pg_size_pretty(pg_total_relation_size('public.' || t.tablename)), COALESCE(s.n_live_tup, 0) FROM pg_tables t LEFT JOIN pg_stat_user_tables s ON t.tablename = s.relname WHERE t.schemaname = 'public' ORDER BY t.tablename" },
            DatabaseType.MySQL => BuildMysqlCmd(creds, "-D", database, "-e", "SELECT TABLE_NAME, TABLE_ROWS, ROUND(((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024), 2) AS 'Size_MB' FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME"),
            DatabaseType.MongoDB => new[] { "mongosh", "--quiet", database, "--eval", "db.getCollectionNames().forEach(c => { var s = db[c].stats(); print(c + '|' + s.count + '|' + s.size) })" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0) return new();

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId, dbType: dbType, creds: creds, ct: ct);
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

    public async Task<List<ColumnInfo>> GetSchemaAsync(string containerId, string database, string table, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null,
        CancellationToken ct = default)
    {
        Withhold(creds);
        // INV-19: as in GetTablesAsync, the database name is a client operand of a read.
        CheckDatabaseName(database, dbType);
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

        var (stdout, _, exitCode) = await ExecInContainer(containerId, cmd, serverId, dbType: dbType, creds: creds, ct: ct);
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

    public async Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null, string? containerName = null)
    {
        Withhold(creds);
        // INV-19: the name is an operand of pg_dump / mysqldump / mongodump (a leading '-' would be an option such as
        // --file=) and part of the dump's file name, which docker cp writes as root on the host (no '/', so it stays in the backup directory).
        CheckDatabaseName(database, dbType);
        // The container is part of the file name (DbDumpFiles): a retention prunes one container's dumps of one
        // database, never a same-named database's in another container; the suffix keeps same-second dumps apart.
        var fileName = DbDumpFiles.NewFileName(DbDumpFiles.ContainerKey(containerName, containerId), database, DateTime.UtcNow);
        var backupPath = $"/tmp/{fileName}";

        // Interpolated values run through `sh -c`, so every one is POSIX-single-quoted (Sq) to prevent shell breakage or
        // injection from names containing spaces or metacharacters. The password is not among them: pg_dump and mysqldump
        // read it from PGPASSWORD / MYSQL_PWD, which ExecInContainer passes beside the command (CodeQL #23–#26).
        var cmd = dbType switch
        {
            DatabaseType.PostgreSQL => new[] { "sh", "-c", $"pg_dump -U {Sq(creds.User)} {Sq(database)} > {Sq(backupPath)}" },
            DatabaseType.MySQL => new[] { "sh", "-c", $"mysqldump -u {Sq(creds.User)} {Sq(database)} > {Sq(backupPath)}" },
            DatabaseType.MongoDB => new[] { "sh", "-c", $"mongodump --db {Sq(database)} --archive={Sq(backupPath + ".gz")} --gzip" },
            _ => Array.Empty<string>()
        };

        if (cmd.Length == 0)
            return (false, "", 0, $"Backup is not supported for {dbType}.");

        var (stdout, stderr, exitCode) = await ExecInContainer(containerId, cmd, serverId, TimeSpan.FromMinutes(5), dbType, creds);

        if (exitCode != 0)
            return (false, "", 0, stderr.Trim());

        // The dump currently lives in the container's /tmp, which is lost on the next container recreate
        // (image update, redeploy). Copy it out to the host backup dir and delete the in-container copy so
        // the backup is actually durable. The file name is container, DB name, time stamp and suffix (DbDumpFiles); quote it.
        var containerFile = dbType == DatabaseType.MongoDB ? $"{backupPath}.gz" : backupPath;
        var hostFileName = dbType == DatabaseType.MongoDB ? $"{fileName}.gz" : fileName;
        var hostPath = $"{_hostBackupDir}/{hostFileName}";
        var sid = OmittedServer.For<IDatabaseService>(serverId);   // the declared fallback the scope guard resolves too

        await _hostExec.ExecuteAsync(sid, $"mkdir -p {ShellUtils.Quote(_hostBackupDir)}", TimeSpan.FromSeconds(5));
        var cp = await _hostExec.ExecuteAsync(sid,
            $"docker cp -- {ShellUtils.Quote(CommandOperands.RequireContainerRef(containerId) + ":" + containerFile)} {ShellUtils.Quote(hostPath)} 2>&1",
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
        DatabaseType.Neo4j => new[] { "cypher-shell", "-u", creds.User, query },
        _ => new[] { "echo", "Unsupported database type" }
    };

    /// <summary>INV-19: the one check for a database name before it becomes a client operand or part of a host file
    /// name (<see cref="CommandOperands.RequireDatabaseName"/>; for MongoDB without '.'). Throws
    /// <see cref="UnsafeOperandException"/>; nothing has run then.</summary>
    public static string CheckDatabaseName(string? database, DatabaseType dbType) =>
        CommandOperands.RequireDatabaseName(database, mongo: dbType == DatabaseType.MongoDB);

    /// <summary>The database password as the commands carry it — as given, single-quoted once (<c>sh -c</c>) and once
    /// more (an operand of <c>docker exec</c>) — is a known secret from now on (<see cref="KnownSecrets"/>), so a command
    /// the executor logs or a sink stores shows <c>***</c> for it, also where it contains a space or a quote that the
    /// <c>PGPASSWORD=…</c> patterns stop at (CodeQL #23–#26). Values under 8 characters are left to those patterns.</summary>
    internal static void Withhold(DatabaseCredentials creds)
    {
        if (string.IsNullOrEmpty(creds.Password)) return;
        var once = creds.Password.Replace("'", "'\\''");
        KnownSecrets.Register(creds.Password, once, once.Replace("'", "'\\''"));
    }

    /// <summary>POSIX single-quote escaping for values interpolated into a <c>sh -c</c> string.</summary>
    private static string Sq(string? s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    // The password from MYSQL_PWD (ExecInContainer), never -p<pw> in argv; plain argv elements, no shell.
    private static string[] BuildMysqlCmd(DatabaseCredentials creds, params string[] extraArgs) =>
        new[] { "mysql", "-u", creds.User }.Concat(extraArgs).ToArray();

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
