using System.Diagnostics;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>R1-33: the parts of the PostgreSQL client-tool runner that need no server — how the connection reaches the
/// tools, a missing tool, and which archives the panel offers for a restore. The round trip itself runs against a real
/// server in <see cref="PostgresBackupRestoreTests"/>.</summary>
public class PostgresDumpToolTests
{
    [Fact]
    public void The_connection_reaches_the_tools_through_their_environment_and_the_password_never_their_command_line()
    {
        var start = new ProcessStartInfo("pg_dump");
        start.ArgumentList.Add("--format=custom");
        start.Environment["PGSERVICE"] = "elsewhere";           // inherited libpq settings may not redirect the tools
        start.Environment["PGPASSFILE"] = "/somewhere/.pgpass";

        PostgresDumpTool.Connect(start, "Host=db.internal;Port=5433;Database=whiskers;Username=app;Password=pw-value;SSL Mode=Require");

        Assert.Equal("db.internal", start.Environment["PGHOST"]);
        Assert.Equal("5433", start.Environment["PGPORT"]);
        Assert.Equal("whiskers", start.Environment["PGDATABASE"]);
        Assert.Equal("app", start.Environment["PGUSER"]);
        Assert.Equal("pw-value", start.Environment["PGPASSWORD"]);
        Assert.Equal("require", start.Environment["PGSSLMODE"]);
        Assert.False(start.Environment.ContainsKey("PGSERVICE"));
        Assert.Equal("/somewhere/.pgpass", start.Environment["PGPASSFILE"]);   // Npgsql would read it too
        Assert.DoesNotContain(start.ArgumentList, a => a.Contains("pw-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Host=db1,db2;Database=w;Username=u;Password=p")]
    [InlineData("Host=db1:5433;Database=w;Username=u;Password=p")]
    public void A_host_list_is_refused_instead_of_reaching_the_tools_as_one_name(string connection)
        => Assert.Throws<PostgresToolException>(() => PostgresDumpTool.Connect(new ProcessStartInfo("pg_dump"), connection));

    [Fact]
    public void Quoted_values_in_an_error_are_left_out_and_names_kept()
    {
        var tail = PostgresDumpTool.Tail(
            "psql:<stdin>:7: ERROR:  invalid input syntax for type uuid: \"not a uuid, but row text\"\n" +
            "psql:<stdin>:9: ERROR:  relation \"Webhooks\" does not exist\n");
        Assert.DoesNotContain("row text", tail, StringComparison.Ordinal);
        Assert.Contains("\"Webhooks\"", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ssl_key_password_is_refused_instead_of_being_dropped()
        => Assert.Throws<PostgresToolException>(() => PostgresDumpTool.Connect(new ProcessStartInfo("pg_dump"),
            "Host=db;Database=w;Username=u;Password=p;SSL Certificate=/c.crt;SSL Key=/c.key;SSL Password=kp"));

    private static async Task<string> FeedAsync(string script, string? probe, bool commit, string? sourceFailure = null,
        MemoryStream? output = null)
    {
        using var input = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(script)));
        output ??= new MemoryStream();
        await using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true))
            await PostgresDumpTool.FeedAsync(input, writer, probe, commit, CancellationToken.None,
                () => Task.FromResult(sourceFailure));
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public async Task A_restore_script_is_wrapped_in_one_transaction_that_replaces_the_schema()
    {
        var wrapped = await FeedAsync(
            "--\n-- PostgreSQL database dump\n--\n\n\\restrict k3y\n\nSET statement_timeout = 0;\nSET transaction_timeout = 0;\nCREATE SCHEMA public;\nCREATE TABLE t (id int);\n\\unrestrict k3y\n",
            probe: "SELECT 1", commit: false);
        var lines = wrapped.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["BEGIN;", "SET LOCAL lock_timeout = '60s';", "SELECT pg_catalog.txid_current() AS whiskers_xid \\gset",
            "DROP SCHEMA IF EXISTS public CASCADE;", "CREATE SCHEMA public;"], lines[..5]);
        Assert.Single(lines, l => l.StartsWith("CREATE SCHEMA", StringComparison.Ordinal));   // the dump's own left out
        Assert.DoesNotContain("SET transaction_timeout = 0;", lines);                             // older servers lack it
        Assert.Contains(lines, l => l.Contains("set_config('transaction_timeout'", StringComparison.Ordinal));
        Assert.DoesNotContain("SET statement_timeout = 0;", lines);                               // bounded by the time limit
        Assert.Contains("SET statement_timeout = '1800000';", lines);
        // After the script: the same transaction or nothing more is committed.
        Assert.Equal(["SELECT pg_catalog.txid_current() = :'whiskers_xid'::bigint AS whiskers_same \\gset", "\\if :whiskers_same",
            "SET search_path = public;", "SELECT 1", "ROLLBACK;", "\\else"], lines[^8..^2]);
        Assert.StartsWith("DO $guard$ BEGIN RAISE EXCEPTION", lines[^2], StringComparison.Ordinal);
        Assert.Equal("\\endif", lines[^1]);
        Assert.DoesNotContain("\r", wrapped);
        Assert.Contains("COMMIT;\n\\else", await FeedAsync("\\restrict k\nSELECT 1;\n\\unrestrict k\n", null, commit: true));
    }

    [Theory]
    [InlineData("\\restrict k\nCREATE TABLE t (id int);\n", null)]                       // cut off before its end
    [InlineData("\\restrict k\nCREATE TABLE t (id int);\n\\unrestrict k\n", "pg_restore failed (exit code 1): read error")]
    public async Task A_script_that_did_not_end_cleanly_is_never_committed(string script, string? sourceFailure)
    {
        var output = new MemoryStream();
        await Assert.ThrowsAsync<PostgresToolException>(() => FeedAsync(script, null, commit: true, sourceFailure, output));
        Assert.DoesNotContain("COMMIT", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VerifyFull", null, "system")]
    [InlineData("VerifyCA", "/certs/ca.pem", "/certs/ca.pem")]
    [InlineData("Require", null, null)]
    public void Certificate_verification_follows_the_app(string mode, string? root, string? expected)
    {
        var start = new ProcessStartInfo("pg_dump");
        PostgresDumpTool.Connect(start, $"Host=db;Database=w;Username=u;Password=p;SSL Mode={mode}" + (root is null ? "" : $";Root Certificate={root}"));
        Assert.Equal(expected, start.Environment.TryGetValue("PGSSLROOTCERT", out var value) ? value : null);
    }

    [Theory]
    [InlineData("\\! touch /tmp/owned\n")]                              // a meta-command before any protection
    [InlineData("--\nCREATE TABLE t (id int);\n\\! touch /tmp/owned\n")] // tools without \restrict
    [InlineData("")]                                                     // nothing at all
    public async Task A_restore_script_without_the_meta_command_guard_is_not_run(string script)
    {
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => FeedAsync(script, null, commit: true));
        Assert.DoesNotContain("touch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_errors_keep_the_error_and_drop_the_lines_that_quote_row_data()
    {
        var tail = PostgresDumpTool.Tail(
            "psql:<stdin>:42: ERROR:  duplicate key value violates unique constraint \"PK_Users\"\n" +
            "DETAIL:  Key (\"Id\")=(alice-secret-row) already exists.\n" +
            "CONTEXT:  COPY AspNetUsers, line 1: \"alice-secret-row\thash-value\"\n" +
            "LINE 1: INSERT INTO x VALUES ('hash-value')\n" +
            "                               ^\n");

        Assert.Contains("duplicate key value", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-secret-row", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-value", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_tool_is_named_with_the_setting_that_points_at_it()
    {
        var tools = new PostgresDumpTool(Path.Combine(Path.GetTempPath(), $"no-pg-bin-{Guid.NewGuid():N}"));

        var ex = await Assert.ThrowsAsync<PostgresToolException>(() =>
            tools.DumpAsync("Host=127.0.0.1;Database=x;Username=u;Password=secret-value", Path.Combine(Path.GetTempPath(), "x.pgdump")));

        Assert.Contains("pg_dump is not available", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PostgresDumpTool.BinDirKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_postgres_migrations_create_no_extension()
    {
        // The dump covers the public schema, not extensions: a migration that created one would make every archive
        // unrestorable into a database without it.
        var assembly = System.Reflection.Assembly.Load("Whiskers.Migrations.Postgres");
        var migrations = assembly.GetTypes()
            .Where(t => typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.NotEmpty(migrations);
        foreach (var type in migrations)
        {
            var migration = (Microsoft.EntityFrameworkCore.Migrations.Migration)Activator.CreateInstance(type)!;
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            foreach (var operation in migration.UpOperations)
            {
                Assert.DoesNotContain(operation.GetAnnotations(), a => a.Name.StartsWith("Npgsql:PostgresExtension:", StringComparison.Ordinal));
                if (operation is Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation sql)
                    Assert.DoesNotContain("EXTENSION", sql.Sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("sqlite", false, true)]
    [InlineData("postgres", true, true)]
    [InlineData("postgres", false, false)]   // a PostgreSQL archive from before R1-33: the files only
    public void Only_an_archive_that_holds_its_database_is_offered_for_a_restore(string provider, bool includesDatabase, bool restorable)
        => Assert.Equal(restorable, new SelfBackupInfo { Provider = provider, IncludesDatabase = includesDatabase }.Restorable);
}
