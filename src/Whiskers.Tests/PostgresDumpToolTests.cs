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

        Assert.Equal(["BEGIN;", "SET LOCAL lock_timeout = '60s';"], lines[..2]);
        Assert.StartsWith("DO $check$", lines[2], StringComparison.Ordinal);
        Assert.Equal(["SELECT pg_catalog.txid_current() AS whiskers_xid \\gset",
            "DROP SCHEMA IF EXISTS public CASCADE;", "CREATE SCHEMA public;",
            $"SELECT {PostgresDumpTool.OutsideDigest} AS whiskers_outside \\gset"], lines[3..7]);
        Assert.Single(lines, l => l.StartsWith("CREATE SCHEMA", StringComparison.Ordinal));   // the dump's own left out
        Assert.DoesNotContain("SET transaction_timeout = 0;", lines);                             // older servers lack it
        Assert.Contains(lines, l => l.Contains("set_config('transaction_timeout'", StringComparison.Ordinal));
        Assert.DoesNotContain("SET statement_timeout = 0;", lines);                               // bounded by the time limit
        Assert.Contains("SET statement_timeout = '1800000';", lines);
        // After the script: the same transaction or nothing more is committed.
        // The comparison names nothing the script could shadow.
        Assert.Equal(["SET search_path = pg_catalog;",
            "SELECT pg_catalog.txid_current() OPERATOR(pg_catalog.=) :'whiskers_xid'::pg_catalog.int8 AS whiskers_same \\gset",
            $"SELECT {PostgresDumpTool.OutsideDigest} OPERATOR(pg_catalog.=) :'whiskers_outside'::pg_catalog.text AS whiskers_outside_same \\gset",
            "\\if :whiskers_same", "\\if :whiskers_outside_same", PostgresDumpTool.CodeObjectCheck,
            "SET search_path = public;", "SELECT 1", "ROLLBACK;", "\\else"], lines[^15..^5]);
        Assert.Contains("changed role or database settings", lines[^5], StringComparison.Ordinal);
        Assert.Equal(["\\endif", "\\else"], lines[^4..^2]);
        Assert.Contains("ended its transaction early", lines[^2], StringComparison.Ordinal);
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

    [Fact]
    public async Task A_line_longer_than_the_limit_stops_the_restore_before_anything_is_committed()
    {
        var output = new MemoryStream();
        var script = "\\restrict k\nINSERT INTO t VALUES ('" + new string('x', PostgresDumpTool.MaxLineChars) + "');\n\\unrestrict k\n";
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => FeedAsync(script, null, commit: true, output: output));
        Assert.Contains("longer than", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMIT", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_that_is_not_utf8_stops_the_restore_instead_of_being_rewritten()
    {
        // A byte of a LATIN1 dump: decoded leniently it would become U+FFFD and be committed as such.
        var bytes = System.Text.Encoding.ASCII.GetBytes("\\restrict k\nINSERT INTO t VALUES ('caf").Concat(new byte[] { 0xE9 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("');\n\\unrestrict k\n")).ToArray();
        using var input = new StreamReader(new MemoryStream(bytes), new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true));
        var output = new MemoryStream();
        await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true);

        var ex = await Assert.ThrowsAsync<PostgresToolException>(() =>
            PostgresDumpTool.FeedAsync(input, writer, null, commit: true, CancellationToken.None));
        Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_options_inherited_from_the_environment_are_refused_like_options_in_the_connection_string()
    {
        var start = new ProcessStartInfo("pg_dump");
        start.Environment["PGOPTIONS"] = "-c search_path=elsewhere";
        Assert.Throws<PostgresToolException>(() => PostgresDumpTool.Connect(start, "Host=db;Database=w;Username=u;Password=p"));
    }

    [Fact]
    public void A_user_is_taken_from_the_environment_only_when_the_connection_string_names_none()
    {
        var start = new ProcessStartInfo("pg_dump");
        start.Environment["PGUSER"] = "env-user";
        PostgresDumpTool.Connect(start, "Host=db;Database=w;Password=p");
        Assert.Equal("env-user", start.Environment["PGUSER"]);

        start = new ProcessStartInfo("pg_dump");
        start.Environment["PGUSER"] = "env-user";
        PostgresDumpTool.Connect(start, "Host=db;Database=w;Username=app;Password=p");
        Assert.Equal("app", start.Environment["PGUSER"]);
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

    [Fact]
    public void Verify_ca_without_a_root_certificate_uses_the_system_bundle_file_which_libpq_accepts_with_it()
    {
        // libpq takes sslrootcert=system with verify-full only; verify-ca gets the bundle file itself.
        var bundle = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("pg_dump");
            start.Environment["SSL_CERT_FILE"] = bundle;
            PostgresDumpTool.Connect(start, "Host=db;Database=w;Username=u;Password=p;SSL Mode=VerifyCA");
            Assert.Equal(bundle, start.Environment["PGSSLROOTCERT"]);
        }
        finally { File.Delete(bundle); }
    }

    [Fact]
    public void Certificates_and_session_attributes_inherited_from_the_environment_reach_the_tools_like_the_app()
    {
        var start = new ProcessStartInfo("pg_dump");
        start.Environment["PGSSLCERT"] = "/run/secrets/client.crt";
        start.Environment["PGSSLKEY"] = "/run/secrets/client.key";
        start.Environment["PGSSLROOTCERT"] = "/run/secrets/ca.crt";
        start.Environment["PGTARGETSESSIONATTRS"] = "read-write";
        PostgresDumpTool.Connect(start, "Host=db;Database=w;Username=u;Password=p;SSL Mode=VerifyFull");
        Assert.Equal("/run/secrets/client.crt", start.Environment["PGSSLCERT"]);
        Assert.Equal("/run/secrets/client.key", start.Environment["PGSSLKEY"]);
        Assert.Equal("/run/secrets/ca.crt", start.Environment["PGSSLROOTCERT"]);   // not replaced by "system"
        Assert.Equal("read-write", start.Environment["PGTARGETSESSIONATTRS"]);

        // The connection string wins.
        start = new ProcessStartInfo("pg_dump");
        start.Environment["PGSSLCERT"] = "/run/secrets/client.crt";
        PostgresDumpTool.Connect(start, "Host=db;Database=w;Username=u;Password=p;SSL Certificate=/app/own.crt;SSL Key=/app/own.key");
        Assert.Equal("/app/own.crt", start.Environment["PGSSLCERT"]);
    }

    [Fact]
    public async Task The_end_of_the_script_prints_the_marker_that_proves_psql_ran_it()
    {
        var output = new MemoryStream();
        using (var input = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("\\restrict k\nSELECT 1;\n\\unrestrict k\n"))))
        await using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true))
            await PostgresDumpTool.FeedAsync(input, writer, null, commit: true, CancellationToken.None, doneMarker: "whiskers-restore-done-x");
        Assert.Contains("COMMIT;\n\\echo whiskers-restore-done-x\n\\else", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_that_ends_inside_a_copy_block_is_not_run_to_its_commit()
    {
        var output = new MemoryStream();
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => FeedAsync(
            "\\restrict k\nCOMMIT;\nCOPY public.sink FROM stdin;\n\\unrestrict k\n", null, commit: true, output: output));
        Assert.Contains("COPY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\echo", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 18.1 (Ubuntu 18.1-1.pgdg24.04+1)", true)]
    [InlineData("pg_dump (PostgreSQL) 17.6", true)]
    [InlineData("pg_dump (PostgreSQL) 17.5", false)]
    [InlineData("pg_dump (PostgreSQL) 16.10", true)]
    [InlineData("pg_dump (PostgreSQL) 16.4 (Debian 16.4-1)", false)]
    [InlineData("pg_dump (PostgreSQL) 14.19", true)]
    [InlineData("pg_dump (PostgreSQL) 13.22", false)]
    [InlineData("something else", false)]
    public void Only_tools_with_the_meta_command_protection_count_as_able_to_restore(string versionLine, bool canRestore)
        => Assert.Equal(canRestore, PostgresDumpTool.CanRestore(versionLine));

    [Theory]
    [InlineData("ScramSHA256", "scram-sha-256")]
    [InlineData("ScramSHA256,MD5", "scram-sha-256,md5")]
    [InlineData("!None", "!none")]
    public void Required_authentication_reaches_the_tools(string requireAuth, string expected)
    {
        var start = new ProcessStartInfo("pg_dump");
        PostgresDumpTool.Connect(start, $"Host=db;Database=w;Username=u;Password=p;Require Auth={requireAuth}");
        Assert.Equal(expected, start.Environment["PGREQUIREAUTH"]);
    }

    [Fact]
    public void Without_a_host_the_tools_connect_to_localhost_like_the_app_not_to_a_unix_socket()
    {
        var start = new ProcessStartInfo("pg_dump");
        PostgresDumpTool.Connect(start, "Database=w;Username=u;Password=p");
        Assert.Equal("localhost", start.Environment["PGHOST"]);
    }

    [Fact]
    public void A_bare_ipv6_address_is_one_host()
    {
        var start = new ProcessStartInfo("pg_dump");
        PostgresDumpTool.Connect(start, "Host=fd00::5;Database=w;Username=u;Password=p");
        Assert.Equal("fd00::5", start.Environment["PGHOST"]);
    }

    [Theory]
    [InlineData("COPY public.t (a, b) FROM stdin;")]
    [InlineData("COPY public.t (a, b) FROM STDIN WITH (FORMAT text);")]
    public async Task Copy_rows_pass_unchanged_even_when_they_read_like_a_rewritten_statement(string copy)
    {
        var wrapped = await FeedAsync(
            "\\restrict k\n" + copy + "\n\tSET statement_timeout = 0;\nCREATE SCHEMA public;\n\\.\n\\unrestrict k\n",
            null, commit: true);
        Assert.Contains("\n\tSET statement_timeout = 0;\nCREATE SCHEMA public;\n\\.\n", wrapped, StringComparison.Ordinal);
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
