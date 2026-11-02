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
/// that proves, before a restore is committed, that this server accepts the dump.
///
/// The password reaches the tools through their environment (<c>PGPASSWORD</c>), never their command line, and no
/// output of theirs is logged unredacted.
/// </summary>
public sealed class PostgresDumpTool
{
    /// <summary>Directory holding the client tools; without it they are looked up on <c>PATH</c>.</summary>
    public const string BinDirKey = "WHISKERS_PG_BIN_DIR";

    private readonly string? _binDir;
    private readonly TimeSpan _timeout;

    public PostgresDumpTool(string? binDir = null, TimeSpan? timeout = null)
    {
        _binDir = string.IsNullOrWhiteSpace(binDir) ? null : binDir;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public static PostgresDumpTool FromConfiguration(IConfiguration? configuration)
        => new(configuration?[BinDirKey] ?? configuration?["Backup:PostgresBinDir"]);

    /// <summary>Writes a custom-format dump of the <c>public</c> schema to <paramref name="outputPath"/>.</summary>
    public Task DumpAsync(string connectionString, string outputPath, CancellationToken ct = default)
        => RunAsync("pg_dump", connectionString,
            ["--format=custom", "--no-owner", "--no-acl", "--schema=public", "--file", outputPath], ct);

    /// <summary>Reads the dump's table of contents: a file pg_restore cannot read is no dump.</summary>
    public async Task CheckReadableAsync(string dumpPath, CancellationToken ct = default)
    {
        var listing = await RunAsync("pg_restore", null, ["--list", dumpPath], ct);
        if (!listing.Contains("TABLE DATA", StringComparison.Ordinal) && !listing.Contains("TABLE ", StringComparison.Ordinal))
            throw new InvalidDataException("The database dump holds no tables.");
    }

    /// <summary>Restores the dump into the database in one transaction; <paramref name="workDir"/> takes the
    /// generated script (it holds the data: removed afterwards).</summary>
    public Task RestoreAsync(string connectionString, string dumpPath, string workDir, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, workDir, commit: true, ct);

    /// <summary>The restore, rolled back at its end: proves this server accepts the dump, changes nothing.</summary>
    public Task DryRunAsync(string connectionString, string dumpPath, string workDir, CancellationToken ct = default)
        => ApplyAsync(connectionString, dumpPath, workDir, commit: false, ct);

    private async Task ApplyAsync(string connectionString, string dumpPath, string workDir, bool commit, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var script = Path.Combine(workDir, $"restore-{Guid.NewGuid():N}.sql");
        var wrapped = Path.Combine(workDir, $"apply-{Guid.NewGuid():N}.sql");
        try
        {
            await RunAsync("pg_restore", null, ["--no-owner", "--no-acl", "--file", script, dumpPath], ct);
            await WrapAsync(script, wrapped, commit, ct);
            // ON_ERROR_STOP: the first failing statement ends psql, its connection and with it the open transaction.
            await RunAsync("psql", connectionString,
                ["--no-psqlrc", "--quiet", "--set", "ON_ERROR_STOP=1", "--file", wrapped], ct);
        }
        finally
        {
            TryDelete(script);
            TryDelete(wrapped);
        }
    }

    // BEGIN; replace the schema with the dump's; COMMIT (or ROLLBACK for the dry run). The dump may create the schema
    // itself: that line is left out, the schema is created here. The drop takes every table's lock: a session that
    // still holds one makes it fail after a minute instead of waiting for the whole timeout.
    private static async Task WrapAsync(string script, string wrapped, bool commit, CancellationToken ct)
    {
        await using var output = new StreamWriter(wrapped, append: false, new UTF8Encoding(false));
        await output.WriteLineAsync("BEGIN;");
        await output.WriteLineAsync("SET LOCAL lock_timeout = '60s';");
        await output.WriteLineAsync("DROP SCHEMA IF EXISTS public CASCADE;");
        await output.WriteLineAsync("CREATE SCHEMA public;");
        using (var input = new StreamReader(script, Encoding.UTF8))
        {
            while (await input.ReadLineAsync(ct) is { } line)
            {
                if (line.Trim() is "CREATE SCHEMA public;" or "CREATE SCHEMA IF NOT EXISTS public;") continue;
                await output.WriteLineAsync(line);
            }
        }
        await output.WriteLineAsync(commit ? "COMMIT;" : "ROLLBACK;");
    }

    private async Task<string> RunAsync(string tool, string? connectionString, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(_binDir is null ? tool : Path.Combine(_binDir, tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (connectionString is not null) Connect(start, connectionString);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start."); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PostgresToolException(
                $"{tool} is not available ({ex.Message}). The PostgreSQL client tools are needed for a backup of a PostgreSQL " +
                $"database; install them or point {BinDirKey} at their directory.");
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                throw new PostgresToolException($"{tool} did not finish within {_timeout.TotalMinutes:0} minutes.");
            }
            var output = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0)
                throw new PostgresToolException($"{tool} failed (exit code {process.ExitCode}): {Tail(errors)}");
            return output;
        }
    }

    /// <summary>The libpq environment for the connection string: the password never on the command line.</summary>
    internal static void Connect(ProcessStartInfo start, string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var env = start.Environment;
        // Nothing from the parent's libpq environment may point the tools elsewhere.
        foreach (var key in env.Keys.Where(k => k.StartsWith("PG", StringComparison.Ordinal)).ToList()) env.Remove(key);
        Set("PGHOST", builder.Host);
        Set("PGPORT", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("PGDATABASE", builder.Database);
        Set("PGUSER", builder.Username);
        Set("PGPASSWORD", builder.Password);
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
        Set("PGCONNECT_TIMEOUT", builder.Timeout > 0 ? builder.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        Set("PGAPPNAME", "whiskers-backup");

        void Set(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) env[key] = value;
        }
    }

    // The end of the tool's error output, redacted: it names the failure, never a secret.
    private static string Tail(string errors)
    {
        var text = errors.Trim();
        if (text.Length > 1000) text = "…" + text[^1000..];
        return Whiskers.Utils.SinkRedaction.Text(text) ?? "";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort: the staging directory goes too */ }
    }
}

/// <summary>A PostgreSQL client tool is missing, failed or did not finish.</summary>
public sealed class PostgresToolException(string message) : InvalidOperationException(message);
