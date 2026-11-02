using System.Diagnostics;
using System.Text;
using Npgsql;

namespace Whiskers.Services.Backup;

/// <summary>
/// R1-33: dumps and restores Whiskers' PostgreSQL database with the PostgreSQL client tools (<c>pg_dump</c>,
/// <c>pg_restore</c>, <c>psql</c>; the image ships them). The dump is a consistent snapshot of the <c>public</c> schema —
/// both contexts (metrics and identity) live there, each with its own migration history. A restore replaces that schema
/// in ONE transaction: the schema is dropped, recreated and filled from the dump, and any error rolls all of it back, so
/// the database is either the archive's or untouched. The same script run with a final <c>ROLLBACK</c> is the dry run
/// that proves, before a restore is committed, that this server accepts the dump (and, with the caller's probe, that the
/// start can migrate it).
///
/// The script is piped from <c>pg_restore</c> to <c>psql</c>: it holds the data and never lands on disk. The password
/// reaches the tools through their environment (<c>PGPASSWORD</c>), never their command line; of their error output only
/// the error lines are kept (row data in CONTEXT/LINE/DETAIL lines is dropped) and those are redacted.
/// </summary>
public sealed class PostgresDumpTool
{
    /// <summary>Directory holding the client tools; without it they are looked up on <c>PATH</c>.</summary>
    public const string BinDirKey = "WHISKERS_PG_BIN_DIR";

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string? _binDir;
    private readonly TimeSpan _timeout;

    /// <summary>Minutes a single tool run may take (default 30).</summary>
    public const string TimeoutKey = "WHISKERS_PG_TIMEOUT_MINUTES";

    /// <summary>The tables a dump of Whiskers' database must hold: the migration histories of both contexts.</summary>
    public static readonly IReadOnlyList<string> RequiredTables = ["__EFMigrationsHistory", "__IdentityMigrationsHistory"];

    public PostgresDumpTool(string? binDir = null, TimeSpan? timeout = null)
    {
        _binDir = string.IsNullOrWhiteSpace(binDir) ? null : binDir;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public static PostgresDumpTool FromConfiguration(IConfiguration? configuration)
    {
        var minutes = configuration?[TimeoutKey] ?? configuration?["Backup:PostgresTimeoutMinutes"];
        return new(configuration?[BinDirKey] ?? configuration?["Backup:PostgresBinDir"],
            int.TryParse(minutes, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : null);
    }

    /// <summary>Writes a custom-format dump of the <c>public</c> schema to <paramref name="outputPath"/> (owner-only)
    /// and proves it holds Whiskers' tables: a database whose tables live in another schema (a search path in the
    /// connection string) would otherwise give an empty dump and an archive that claims a database it does not hold.</summary>
    public async Task DumpAsync(string connectionString, string outputPath, CancellationToken ct = default)
    {
        // pg_dump truncates an existing file and keeps its mode: created owner-only first, the data never readable by others.
        await using (File.Create(outputPath)) { }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await RunAsync("pg_dump", connectionString,
            ["--format=custom", "--no-owner", "--no-acl", "--schema=public", "--file", outputPath], ct);
        await CheckReadableAsync(outputPath, ct);
    }

    /// <summary>Reads the dump's table of contents: a file pg_restore cannot read is no dump, and one without the
    /// migration histories in <c>public</c> is no dump of Whiskers' database.</summary>
    public async Task CheckReadableAsync(string dumpPath, CancellationToken ct = default)
    {
        var listing = await RunAsync("pg_restore", null, ["--list", dumpPath], ct);
        // A table entry reads "<id>; <oid> <oid> TABLE public <name> <owner>".
        var missing = RequiredTables.Where(t => !System.Text.RegularExpressions.Regex.IsMatch(listing,
            $@"^\d+; \d+ \d+ TABLE public {System.Text.RegularExpressions.Regex.Escape(t)}(\s|$)", System.Text.RegularExpressions.RegexOptions.Multiline)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"The database dump holds no {string.Join(" or ", missing)} in the public schema: Whiskers' tables are not in it " +
                "(their schema must be public; a search path in the connection string moves them elsewhere).");
    }

    /// <summary>Restores the dump into the database in one transaction.</summary>
    public Task RestoreAsync(string connectionString, string dumpPath, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, probeSql: null, commit: true, ct);

    /// <summary>The restore, rolled back at its end: proves this server accepts the dump and changes nothing.
    /// <paramref name="probeSql"/> runs after the dump inside the same transaction (with <c>search_path</c> set to
    /// <c>public</c>); an error in it fails the dry run.</summary>
    public Task DryRunAsync(string connectionString, string dumpPath, string? probeSql = null, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, probeSql, commit: false, ct);

    // pg_restore writes the script to stdout; it is checked and wrapped line by line on its way into psql's stdin:
    // BEGIN; replace the schema with the dump's; [probe;] COMMIT (or ROLLBACK for the dry run).
    private async Task ApplyAsync(string connectionString, string dumpPath, string? probeSql, bool commit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var restore = Start("pg_restore", null, ["--no-owner", "--no-acl", "--file", "-", dumpPath], stdin: false);
        Process psql;
        try
        {
            // ON_ERROR_STOP: the first failing statement ends psql, its connection and with it the open transaction.
            psql = Start("psql", connectionString, ["--no-psqlrc", "--quiet", "--set", "ON_ERROR_STOP=1", "--file", "-"], stdin: true);
        }
        catch
        {
            Kill(restore);
            throw;
        }
        using (psql)
        {
            var restoreErrors = restore.StandardError.ReadToEndAsync(timeout.Token);
            var psqlOutput = psql.StandardOutput.ReadToEndAsync(timeout.Token);
            var psqlErrors = psql.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                try
                {
                    await FeedAsync(restore.StandardOutput, psql.StandardInput, probeSql, commit, timeout.Token);
                }
                catch (IOException)
                {
                    // psql stopped reading (ON_ERROR_STOP): its own error says why, below. Nobody reads pg_restore's
                    // output any more — it would block on the full pipe until the timeout.
                    Kill(restore);
                }
                try { psql.StandardInput.Close(); } catch (IOException) { /* already gone */ }
                await psql.WaitForExitAsync(timeout.Token);
                if (psql.ExitCode != 0) Kill(restore);
                await restore.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                // A refused script, a timeout or a cancellation: psql is killed before it could see a COMMIT, the server
                // rolls the open transaction back with the connection.
                Kill(psql);
                Kill(restore);
                if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                    throw new PostgresToolException($"The restore did not finish within {_timeout.TotalMinutes:0} minutes.");
                throw;
            }
            await psqlOutput;
            if (psql.ExitCode != 0)
                throw new PostgresToolException($"psql failed (exit code {psql.ExitCode}): {Tail(await psqlErrors)}");
            if (restore.ExitCode != 0)
                throw new PostgresToolException($"pg_restore failed (exit code {restore.ExitCode}): {Tail(await restoreErrors)}");
        }
    }

    internal static async Task FeedAsync(StreamReader script, StreamWriter psql, string? probeSql, bool commit, CancellationToken ct)
    {
        psql.NewLine = "\n";
        await psql.WriteLineAsync("BEGIN;");
        // The drop takes every table's lock: a session that still holds one makes it fail after a minute instead of
        // waiting for the whole timeout.
        await psql.WriteLineAsync("SET LOCAL lock_timeout = '60s';");
        await psql.WriteLineAsync("DROP SCHEMA IF EXISTS public CASCADE;");
        await psql.WriteLineAsync("CREATE SCHEMA public;");

        // The script comes from an archive: a crafted dump could carry psql meta-commands (\! runs a shell command).
        // pg_restore since 17.6 opens its script with \restrict, which makes psql refuse every meta-command until the
        // matching \unrestrict (CVE-2025-8714); a script that does not open with it is not run.
        var restricted = false;
        while (await script.ReadLineAsync(ct) is { } line)
        {
            if (!restricted)
            {
                if (line.StartsWith(@"\restrict ", StringComparison.Ordinal)) restricted = true;
                else if (line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
                    throw new PostgresToolException(
                        @"pg_restore did not protect its script against meta-commands (\restrict): PostgreSQL client tools 17.6 or newer are needed to restore a dump.");
            }
            switch (line.Trim())
            {
                // The schema is created above.
                case "CREATE SCHEMA public;" or "CREATE SCHEMA IF NOT EXISTS public;":
                    continue;
                // Client 17+ writes this whatever the server; servers before 17 do not know the setting.
                case "SET transaction_timeout = 0;":
                    await psql.WriteLineAsync(
                        "SELECT pg_catalog.set_config('transaction_timeout', '0', false) WHERE pg_catalog.current_setting('server_version_num')::int >= 170000;");
                    continue;
            }
            await psql.WriteLineAsync(line);
        }
        if (!restricted)
            throw new PostgresToolException("The database dump produced an empty restore script.");

        if (probeSql is not null)
        {
            // The dump empties search_path; the probe names the tables unqualified, as the app does.
            await psql.WriteLineAsync("SET search_path = public;");
            await psql.WriteLineAsync(probeSql);
        }
        await psql.WriteLineAsync(commit ? "COMMIT;" : "ROLLBACK;");
        await psql.FlushAsync(ct);
    }

    private async Task<string> RunAsync(string tool, string? connectionString, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var process = Start(tool, connectionString, arguments, stdin: false);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            throw new PostgresToolException($"{tool} did not finish within {_timeout.TotalMinutes:0} minutes.");
        }
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
            throw new PostgresToolException($"{tool} failed (exit code {process.ExitCode}): {Tail(errors)}");
        return output;
    }

    private Process Start(string tool, string? connectionString, IReadOnlyList<string> arguments, bool stdin)
    {
        var start = new ProcessStartInfo(_binDir is null ? tool : Path.Combine(_binDir, tool))
        {
            RedirectStandardInput = stdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (stdin) start.StandardInputEncoding = Utf8;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (connectionString is not null) Connect(start, connectionString);
        try { return Process.Start(start) ?? throw new PostgresToolException($"{tool} did not start."); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PostgresToolException(
                $"{tool} is not available ({ex.Message}). The PostgreSQL client tools are needed for a backup of a PostgreSQL " +
                $"database; install them or point {BinDirKey} at their directory.");
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>The libpq environment for the connection string: the password never on the command line.</summary>
    internal static void Connect(ProcessStartInfo start, string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.SslPassword))
            throw new PostgresToolException(
                "The connection string sets an SSL key password, which the PostgreSQL client tools cannot take from Whiskers. Use an unencrypted client key for the backup role.");
        // libpq takes one host; Npgsql's lists and host:port forms would reach the tools as a name they cannot resolve.
        if (builder.Host is { } host && (host.Contains(',') || host.Contains(':')))
            throw new PostgresToolException(
                "The connection string names several hosts or a host with a port; the backup needs one host in Host and the port in Port.");
        var env = start.Environment;
        // Without a password in the connection string, Npgsql reads PGPASSWORD / PGPASSFILE: the tools may too.
        var inheritedPassword = string.IsNullOrEmpty(builder.Password) ? (env.TryGetValue("PGPASSWORD", out var pw) ? pw : null) : null;
        var inheritedPassfile = env.TryGetValue("PGPASSFILE", out var pf) ? pf : null;
        // Nothing else from the parent's libpq environment may point the tools elsewhere.
        foreach (var key in env.Keys.Where(k => k.StartsWith("PG", StringComparison.Ordinal)).ToList()) env.Remove(key);
        Set("PGHOST", builder.Host);
        Set("PGPORT", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("PGDATABASE", builder.Database);
        Set("PGUSER", builder.Username);
        Set("PGPASSWORD", string.IsNullOrEmpty(builder.Password) ? inheritedPassword : builder.Password);
        Set("PGPASSFILE", builder.Passfile ?? inheritedPassfile);
        Set("PGSSLMODE", builder.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer",
        });
        Set("PGSSLROOTCERT", builder.RootCertificate);
        Set("PGSSLCERT", builder.SslCertificate);
        Set("PGSSLKEY", builder.SslKey);
        Set("PGTARGETSESSIONATTRS", builder.TargetSessionAttributes);
        Set("PGCONNECT_TIMEOUT", builder.Timeout > 0 ? builder.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        Set("PGAPPNAME", "whiskers-backup");

        void Set(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) env[key] = value;
        }
    }

    /// <summary>The tool's error lines, redacted: they name the failure. The lines that quote the failing statement or
    /// row (LINE, CONTEXT, DETAIL, STATEMENT, the caret line) are left out — they can hold column values.</summary>
    internal static string Tail(string errors)
    {
        var kept = errors.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0 && !char.IsWhiteSpace(l[0]))
            .Where(l => !ErrorDetail(l))
            .ToList();
        // A quoted value that is not a name (an input value, a row's text) is left out.
        var text = string.Join(" | ", kept.TakeLast(5).Select(l => System.Text.RegularExpressions.Regex.Replace(l,
            "\"([^\"]*)\"", q => System.Text.RegularExpressions.Regex.IsMatch(q.Groups[1].Value, @"^[A-Za-z_][A-Za-z0-9_$.]{0,62}$") ? q.Value : "\"…\"")));
        if (text.Length > 500) text = text[..500] + "…";
        return Whiskers.Utils.SinkRedaction.Text(text) ?? "";

        // psql prefixes server messages with "psql:<file>:<line>: "; the severity follows.
        static bool ErrorDetail(string line)
        {
            var message = System.Text.RegularExpressions.Regex.Replace(line, @"^psql:\S*: ", "");
            return message.StartsWith("LINE ", StringComparison.Ordinal)
                   || message.StartsWith("CONTEXT:", StringComparison.Ordinal)
                   || message.StartsWith("DETAIL:", StringComparison.Ordinal)
                   || message.StartsWith("STATEMENT:", StringComparison.Ordinal)
                   || message.StartsWith("QUERY:", StringComparison.Ordinal)
                   || message.StartsWith("WHERE:", StringComparison.Ordinal);
        }
    }
}

/// <summary>A PostgreSQL client tool is missing, failed or did not finish.</summary>
public sealed class PostgresToolException(string message) : InvalidOperationException(message);
