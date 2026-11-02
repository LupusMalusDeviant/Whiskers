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
    // The dump is written in UTF-8 (pg_dump --encoding=UTF8); a byte that is not valid UTF-8 stops the restore instead
    // of reaching the database as a replacement character.
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

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

    /// <summary>The time limit from the configuration; the tools' directory from the process environment only, since
    /// the configuration also holds the data directory's settings files (written by the UI and by a restore), which
    /// must not choose the programs Whiskers runs.</summary>
    public static PostgresDumpTool FromConfiguration(IConfiguration? configuration)
    {
        var minutes = configuration?[TimeoutKey] ?? configuration?["Backup:PostgresTimeoutMinutes"];
        return new(Environment.GetEnvironmentVariable(BinDirKey),
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
            ["--format=custom", "--encoding=UTF8", "--no-owner", "--no-acl", "--schema=public", "--file", outputPath], ct);
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

    /// <summary>The version line of <c>pg_dump</c> (the readiness check: without the tools no backup).</summary>
    public async Task<string> VersionAsync(CancellationToken ct = default)
        => (await RunAsync("pg_dump", null, ["--version"], ct)).Trim();

    /// <summary>Restores the dump into the database in one transaction.</summary>
    public Task RestoreAsync(string connectionString, string dumpPath, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, probeSql: null, commit: true, ct);

    /// <summary>The restore, rolled back at its end: proves this server accepts the dump and changes nothing.
    /// <paramref name="probeSql"/> runs after the dump inside the same transaction (with <c>search_path</c> set to
    /// <c>public</c>); an error in it fails the dry run.</summary>
    public Task DryRunAsync(string connectionString, string dumpPath, string? probeSql = null, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, probeSql, commit: false, ct);

    /// <summary>Tests only: a given restore script, wrapped and fed to psql as a dump's would be (no pg_restore), so
    /// that a script a crafted dump could produce can be run against a real server.</summary>
    internal async Task ApplyScriptAsync(string connectionString, string script, bool commit)
    {
        using var psql = Start("psql", connectionString, ["--no-psqlrc", "--quiet", "--set", "ON_ERROR_STOP=1", "--file", "-"], stdin: true);
        var output = psql.StandardOutput.ReadToEndAsync();
        var errors = psql.StandardError.ReadToEndAsync();
        using (var reader = new StreamReader(new MemoryStream(Utf8.GetBytes(script))))
        {
            try { await FeedAsync(reader, psql.StandardInput, null, commit, CancellationToken.None, null, _timeout); }
            catch (IOException) { /* psql stopped: its error says why */ }
        }
        try { psql.StandardInput.Close(); } catch (IOException) { /* already gone */ }
        await psql.WaitForExitAsync();
        await output;
        if (psql.ExitCode != 0)
            throw new PostgresToolException($"psql failed (exit code {psql.ExitCode}): {Tail(await errors)}");
    }

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
                    // pg_restore must have ended cleanly before anything is committed: a dump cut off on the volume
                    // would otherwise end the script early and COMMIT half a schema.
                    await FeedAsync(restore.StandardOutput, psql.StandardInput, probeSql, commit, timeout.Token,
                        sourceFailure: async () =>
                        {
                            await restore.WaitForExitAsync(timeout.Token);
                            return restore.ExitCode == 0
                                ? null
                                : $"pg_restore failed (exit code {restore.ExitCode}): {Tail(await restoreErrors)}";
                        }, _timeout);
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

    // Whiskers' migrations create tables, sequences, indexes and constraints only. Code that a dump brings into public —
    // a function, a trigger, a rule, a view, a row policy, an operator — would run with the app's rights on its reads
    // and writes, so a restore that leaves any of it is refused (the SQLite restore refuses triggers and views alike).
    // Runs with search_path = pg_catalog and every name qualified, after the transaction-id guard.
    internal const string CodeObjectCheck =
        "DO $code$ BEGIN IF " +
        "EXISTS (SELECT 1 FROM pg_catalog.pg_proc p WHERE p.pronamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace) " +
        "OR EXISTS (SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) t.tgrelid " +
        "WHERE NOT t.tgisinternal AND c.relnamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace) " +
        "OR EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite r JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) r.ev_class " +
        "WHERE c.relnamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace) " +
        "OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c WHERE c.relnamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace " +
        "AND c.relkind OPERATOR(pg_catalog.=) ANY (ARRAY['v', 'm']::pg_catalog.\"char\"[])) " +
        "OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy y JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) y.polrelid " +
        "WHERE c.relnamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace) " +
        "OR EXISTS (SELECT 1 FROM pg_catalog.pg_operator o WHERE o.oprnamespace OPERATOR(pg_catalog.=) 'public'::pg_catalog.regnamespace) " +
        "THEN RAISE EXCEPTION 'the database dump brings functions, triggers, rules, views, policies or operators into public, which Whiskers does not create'; " +
        "END IF; END $code$;";

    internal static async Task FeedAsync(StreamReader script, StreamWriter psql, string? probeSql, bool commit, CancellationToken ct,
        Func<Task<string?>>? sourceFailure = null, TimeSpan? statementTimeout = null)
    {
        psql.NewLine = "\n";
        psql.AutoFlush = false;   // one write per buffer, not per line; flushed at the end
        await psql.WriteLineAsync("BEGIN;");
        // The drop takes every table's lock: a session that still holds one makes it fail after a minute instead of
        // waiting for the whole timeout.
        await psql.WriteLineAsync("SET LOCAL lock_timeout = '60s';");
        // A killed psql (time limit, cancellation) must not leave its statement running under the schema's locks: the
        // server checks the connection every 10 s (PostgreSQL 14+ on Linux; elsewhere the setting is refused and skipped).
        await psql.WriteLineAsync(
            "DO $check$ BEGIN PERFORM pg_catalog.set_config('client_connection_check_interval', '10000', false); " +
            "EXCEPTION WHEN OTHERS THEN NULL; END $check$;");
        // The transaction's id, kept in a psql variable before the script runs. A script restricted by \restrict cannot
        // set psql variables, so a crafted dump that ends the transaction early (its own COMMIT) cannot hide it: the id
        // after the script differs, and nothing more is committed. This detects such a script; what it changed before is
        // undone only by the pre-restore safety backup, which the caller takes first.
        await psql.WriteLineAsync(@"SELECT pg_catalog.txid_current() AS whiskers_xid \gset");
        await psql.WriteLineAsync("DROP SCHEMA IF EXISTS public CASCADE;");
        await psql.WriteLineAsync("CREATE SCHEMA public;");

        // The script comes from an archive: a crafted dump could carry psql meta-commands (\! runs a shell command).
        // pg_restore since 17.6 (and 16.10, 15.14, 14.19) opens its script with \restrict, which makes psql refuse every
        // meta-command until the matching \unrestrict with a key of that run (CVE-2025-8714); a script that does not
        // open with it is not run.
        var restricted = false;
        string? last = null;
        var statementLimit = $"SET statement_timeout = '{(long)(statementTimeout ?? TimeSpan.FromMinutes(30)).TotalMilliseconds}';";
        var lines = new BoundedLineReader(script);
        while (await lines.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim().Length > 0) last = line;
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
                // No statement may outlive the tool's time limit: a killed psql leaves the server running it under the
                // schema's locks until it notices the closed connection.
                case "SET statement_timeout = 0;":
                    await psql.WriteLineAsync(statementLimit);
                    continue;
            }
            await psql.WriteLineAsync(line);
        }
        if (!restricted)
            throw new PostgresToolException("The database dump produced an empty restore script.");
        // Nothing is committed unless the script reached its own end and pg_restore finished without an error.
        if (sourceFailure is not null && await sourceFailure() is { } failure)
            throw new PostgresToolException(failure);
        if (last is null || !last.StartsWith(@"\unrestrict ", StringComparison.Ordinal))
            throw new PostgresToolException(@"The restore script ended before its end (\unrestrict): the dump is incomplete.");

        // The comparison names nothing the script could have created: the script may own functions, operators and types
        // in public and set the search path, so the search path is pg_catalog alone and operator and type are qualified.
        await psql.WriteLineAsync("SET search_path = pg_catalog;");
        await psql.WriteLineAsync(
            @"SELECT pg_catalog.txid_current() OPERATOR(pg_catalog.=) :'whiskers_xid'::pg_catalog.int8 AS whiskers_same \gset");
        await psql.WriteLineAsync(@"\if :whiskers_same");
        await psql.WriteLineAsync(CodeObjectCheck);
        if (probeSql is not null)
        {
            // The dump empties search_path; the probe names the tables unqualified, as the app does.
            await psql.WriteLineAsync("SET search_path = public;");
            await psql.WriteLineAsync(probeSql);
        }
        await psql.WriteLineAsync(commit ? "COMMIT;" : "ROLLBACK;");
        await psql.WriteLineAsync(@"\else");
        await psql.WriteLineAsync("DO $guard$ BEGIN RAISE EXCEPTION 'the restore script ended its transaction early'; END $guard$;");
        await psql.WriteLineAsync(@"\endif");
        await psql.FlushAsync(ct);
    }

    private async Task<string> RunAsync(string tool, string? connectionString, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var process = Start(tool, connectionString, arguments, stdin: false);
        var stdout = ReadBoundedAsync(process, timeout.Token);
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

    // The output of the tools run this way (a version line, a dump's table of contents) is small; a crafted archive with
    // millions of entries must not fill the app's memory: more than 16 Mi characters stops the tool.
    private const int MaxOutputChars = 16 << 20;

    private static async Task<string> ReadBoundedAsync(Process process, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[81920];
        int read;
        while ((read = await ReadAsync()) > 0)
        {
            if (output.Length + read > MaxOutputChars)
            {
                Kill(process);
                throw new PostgresToolException($"The tool's output exceeds {MaxOutputChars / (1 << 20)} Mi characters.");
            }
            output.Append(buffer, 0, read);
        }
        return output.ToString();

        async Task<int> ReadAsync()
        {
            try { return await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct); }
            catch (DecoderFallbackException)
            {
                Kill(process);
                throw new PostgresToolException("The tool's output is not valid UTF-8.");
            }
        }
    }

    // The longest line of a restore script: a crafted archive with one entry without a line break must not make the app
    // hold it whole. Lines of an honest dump are statements and COPY rows, far below it.
    internal const int MaxLineChars = 64 << 20;

    // StreamReader.ReadLineAsync holds a line whole, however long; this reader stops at MaxLineChars. A line ends at
    // "\n" (a "\r" before it is dropped), as in the script pg_restore writes; COPY rows escape their own line breaks.
    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[81920];
        private readonly StringBuilder _line = new();
        private int _position, _length;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            _line.Clear();
            var any = false;
            while (true)
            {
                if (_position == _length)
                {
                    try { _length = await reader.ReadAsync(_buffer.AsMemory(), ct); }
                    catch (DecoderFallbackException)
                    {
                        throw new PostgresToolException("The restore script is not valid UTF-8: the dump was not written by this version's backup.");
                    }
                    _position = 0;
                    if (_length == 0) return any ? Done() : null;
                }
                any = true;
                var end = Array.IndexOf(_buffer, '\n', _position, _length - _position);
                var take = (end < 0 ? _length : end) - _position;
                if (_line.Length + take > MaxLineChars)
                    throw new PostgresToolException($"The restore script holds a line longer than {MaxLineChars / (1 << 20)} Mi characters.");
                _line.Append(_buffer, _position, take);
                _position += take;
                if (end >= 0)
                {
                    _position++;
                    return Done();
                }
            }
        }

        private string Done()
        {
            if (_line.Length > 0 && _line[^1] == '\r') _line.Length--;
            return _line.ToString();
        }
    }

    private Process Start(string tool, string? connectionString, IReadOnlyList<string> arguments, bool stdin)
    {
        var start = new ProcessStartInfo(_binDir is null ? tool : Path.Combine(_binDir, tool))
        {
            RedirectStandardInput = stdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = StrictUtf8,
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
        // Server options (-c settings, a search path) would change what the tools see and cannot be checked here.
        if (!string.IsNullOrEmpty(builder.Options))
            throw new PostgresToolException(
                "The connection string sets Options, which the backup does not pass on. Set those server settings on the role or the database instead.");
        // libpq takes one host; Npgsql's lists and host:port forms would reach the tools as a name they cannot resolve.
        if (builder.Host is { } host && (host.Contains(',') || host.Contains(':')))
            throw new PostgresToolException(
                "The connection string names several hosts or a host with a port; the backup needs one host in Host and the port in Port.");
        var env = start.Environment;
        // The same server options from the environment, which Npgsql reads too: refused like Options, not dropped.
        if (env.TryGetValue("PGOPTIONS", out var inheritedOptions) && !string.IsNullOrWhiteSpace(inheritedOptions))
            throw new PostgresToolException(
                "PGOPTIONS is set in the environment, which the backup does not pass on. Set those server settings on the role or the database instead.");
        // Without a user or password in the connection string, Npgsql reads PGUSER / PGPASSWORD / PGPASSFILE: the tools may too.
        var inheritedPassword = string.IsNullOrEmpty(builder.Password) ? (env.TryGetValue("PGPASSWORD", out var pw) ? pw : null) : null;
        var inheritedPassfile = env.TryGetValue("PGPASSFILE", out var pf) ? pf : null;
        var inheritedUser = string.IsNullOrEmpty(builder.Username) && env.TryGetValue("PGUSER", out var pu) ? pu : null;
        // Nothing else from the parent's libpq environment may point the tools elsewhere.
        foreach (var key in env.Keys.Where(k => k.StartsWith("PG", StringComparison.Ordinal)).ToList()) env.Remove(key);
        Set("PGHOST", builder.Host);
        Set("PGPORT", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("PGDATABASE", builder.Database);
        Set("PGUSER", string.IsNullOrEmpty(builder.Username) ? inheritedUser : builder.Username);
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
        // Npgsql verifies against the system's trusted roots when no root certificate is named; libpq would look for
        // ~/.postgresql/root.crt instead. "system" (libpq 16+) makes the tools verify like the app. Require does not
        // verify in either (Npgsql 8+ follows libpq).
        Set("PGSSLROOTCERT", !string.IsNullOrEmpty(builder.RootCertificate) ? builder.RootCertificate
            : builder.SslMode is SslMode.VerifyCA or SslMode.VerifyFull ? "system" : null);
        Set("PGSSLCERT", builder.SslCertificate);
        Set("PGSSLKEY", builder.SslKey);
        Set("PGTARGETSESSIONATTRS", builder.TargetSessionAttributes);
        Set("PGCHANNELBINDING", builder.ChannelBinding switch
        {
            ChannelBinding.Disable => "disable",
            ChannelBinding.Require => "require",
            _ => "prefer",
        });
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
