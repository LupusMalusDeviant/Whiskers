using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// The upgrade path of docs/operations/postgres-18-upgrade.md, end to end with real servers: Whiskers' schema (the
/// PostgreSQL migrations, applied as at a start) and some rows on postgres:17-alpine, the guide's pg_dump in the 17
/// container, its grep check and its single-transaction psql restore into postgres:18-alpine. Afterwards the migration
/// history and every table's row count are equal and the sequences continue. Then the guard's confirm run
/// (WHISKERS_CONFIRM_RESTORE=1, the script from the compose file) against that 18 server under the network alias
/// <c>db</c>: it writes the marker only for a database with Whiskers' migration history.
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresUpgradeTests : IClassFixture<PostgresUpgradeTests.Servers>
{
    private const string MarkerPath = "/new/.whiskers-restored-from-17";
    private readonly Servers _s;

    public PostgresUpgradeTests(Servers servers) => _s = servers;

    public sealed class Servers : IAsyncLifetime
    {
        public INetwork Network { get; } = new NetworkBuilder().Build();
        public PostgreSqlContainer Pg17 { get; }
        public PostgreSqlContainer Pg18 { get; }
        public IContainer Guard { get; }
        public Exception? StartFailure { get; private set; }
        private readonly SemaphoreSlim _once = new(1, 1);
        private bool _upgraded;
        public Snapshot? Before { get; private set; }

        public Servers()
        {
            Pg17 = new PostgreSqlBuilder("postgres:17-alpine").Build();
            Pg18 = new PostgreSqlBuilder("postgres:18-alpine").WithNetwork(Network).WithNetworkAliases("db").Build();
            // The guard's container as the overlay runs it: the 18 image, the db service's user and database, the
            // password as PGPASSWORD. Kept alive so each case runs the script in it.
            Guard = new ContainerBuilder("postgres:18-alpine")
                .WithNetwork(Network)
                .WithEntrypoint("tail", "-f", "/dev/null")
                .WithEnvironment("POSTGRES_USER", "postgres")
                .WithEnvironment("POSTGRES_DB", "postgres")
                .WithEnvironment("PGPASSWORD", "postgres")
                .Build();
        }

        public async Task InitializeAsync()
        {
            try
            {
                await Network.CreateAsync();
            }
            catch (Exception ex)
            {
                StartFailure = ex;
                return;
            }
            StartFailure = await DockerRequirement.TryStartAsync(Pg17)
                           ?? await DockerRequirement.TryStartAsync(Pg18)
                           ?? await DockerRequirement.TryStartAsync(Guard);
        }

        public async Task DisposeAsync()
        {
            await Guard.DisposeAsync();
            await Pg18.DisposeAsync();
            await Pg17.DisposeAsync();
            await Network.DisposeAsync();
        }

        /// <summary>Runs the upgrade once for the class: schema and rows on 17, dump and restore as the guide does.</summary>
        public async Task UpgradeAsync()
        {
            await _once.WaitAsync();
            try
            {
                if (_upgraded) return;
                var options = new DbContextOptionsBuilder<MetricsDbContext>()
                    .UseNpgsql(Pg17.GetConnectionString(), o => o.MigrationsAssembly("Whiskers.Migrations.Postgres"))
                    .Options;
                await using (var db = new MetricsDbContext(options))
                {
                    await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
                    var now = DateTime.UtcNow;
                    for (var i = 0; i < 5; i++)
                        db.AuditLog.Add(new AuditLogEntity { Actor = $"user{i}@example.com", ActorType = "web",
                            Action = "container.restart", TargetType = "container", TargetId = $"c{i}", Details = "Ärger <&>",
                            Timestamp = now.AddMinutes(-i) });
                    db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ContainerName = "web", ServerId = "s1",
                        Timestamp = now, CpuPercent = 3.5 });
                    db.Notifications.Add(new NotificationEntity { NotificationId = "n1", Title = "t", Detail = "d", Timestamp = now,
                        Severity = "Info", Read = false });
                    await db.SaveChangesAsync();
                }
                Before = await Snapshot.TakeAsync(Pg17.GetConnectionString());

                // Step 1 of the guide: c17 exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges' > whiskers-pg17.sql
                var dump = await Pg17.ExecAsync(["sh", "-c", "pg_dump -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" --no-owner --no-privileges"]);
                Assert.True(dump.ExitCode == 0, dump.Stderr);
                // Step 2: grep -q 'PostgreSQL database dump complete' whiskers-pg17.sql && c exec -T db sh -c 'psql … --single-transaction …' < whiskers-pg17.sql
                Assert.Contains("PostgreSQL database dump complete", dump.Stdout);
                await Pg18.CopyAsync(new UTF8Encoding(false).GetBytes(dump.Stdout), "/tmp/whiskers-pg17.sql");
                var restore = await Pg18.ExecAsync(["sh", "-c",
                    "psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 --single-transaction -q < /tmp/whiskers-pg17.sql"]);
                Assert.True(restore.ExitCode == 0, restore.Stderr);
                _upgraded = true;
            }
            finally
            {
                _once.Release();
            }
        }
    }

    public sealed record Snapshot(IReadOnlyList<string> History, IReadOnlyDictionary<string, long> Rows,
        IReadOnlyDictionary<string, long?> Sequences)
    {
        public static async Task<Snapshot> TakeAsync(string connectionString)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            var history = new List<string>();
            await using (var cmd = new NpgsqlCommand("""select "MigrationId" || ' ' || "ProductVersion" from "__EFMigrationsHistory" order by 1""", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) history.Add(r.GetString(0));
            var tables = new List<string>();
            await using (var cmd = new NpgsqlCommand("select quote_ident(table_schema) || '.' || quote_ident(table_name) from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' order by 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) tables.Add(r.GetString(0));
            var rows = new Dictionary<string, long>();
            foreach (var t in tables)
            {
                await using var cmd = new NpgsqlCommand($"select count(*) from {t}", conn);
                rows[t] = (long)(await cmd.ExecuteScalarAsync())!;
            }
            var sequences = new Dictionary<string, long?>();
            await using (var cmd = new NpgsqlCommand("select quote_ident(schemaname) || '.' || quote_ident(sequencename), last_value from pg_sequences where schemaname = 'public' order by 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) sequences[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetInt64(1);
            return new Snapshot(history, rows, sequences);
        }
    }

    private void SkipUnlessUp() => DockerRequirement.SkipUnlessStarted(_s.StartFailure, "The PostgreSQL 17/18 upgrade servers");

    private async Task<(long? Exit, string Out, string Err)> GuardAsync(params string[] env)
    {
        var result = await _s.Guard.ExecAsync(["env", .. env, "sh", "-c", PostgresOverlayTests.GuardScript()]);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    private async Task PrepareVolumesAsync()
    {
        var setup = await _s.Guard.ExecAsync(["sh", "-c",
            "rm -rf /old /new && mkdir -p /old/global /new && echo 17 > /old/PG_VERSION && head -c 8192 /dev/urandom > /old/global/pg_control"]);
        Assert.True(setup.ExitCode == 0, setup.Stderr);
    }

    private async Task<string?> MarkerAsync()
    {
        var read = await _s.Guard.ExecAsync(["sh", "-c", $"cat {MarkerPath}"]);
        return read.ExitCode == 0 ? read.Stdout.Trim() : null;
    }

    [SkippableFact]
    public async Task The_guides_dump_and_restore_carry_history_rows_and_sequences_from_17_to_18()
    {
        SkipUnlessUp();
        await _s.UpgradeAsync();
        var before = _s.Before!;
        var after = await Snapshot.TakeAsync(_s.Pg18.GetConnectionString());

        Assert.NotEmpty(before.History);
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Rows, after.Rows);
        Assert.Equal(5, after.Rows["public.\"AuditLog\""]);
        Assert.Equal(before.Sequences.Keys, after.Sequences.Keys);

        await using var conn = new NpgsqlConnection(_s.Pg18.GetConnectionString());
        await conn.OpenAsync();
        await using (var version = new NpgsqlCommand("show server_version_num", conn))
            Assert.StartsWith("18", (string)(await version.ExecuteScalarAsync())!);
        var used = before.Sequences.Where(s => s.Value is not null).ToList();
        Assert.NotEmpty(used);   // at least the audit log's identity
        foreach (var (name, last) in used)
        {
            await using var next = new NpgsqlCommand($"select nextval('{name.Replace("'", "''")}')", conn);
            Assert.Equal(last + 1, (long)(await next.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task The_confirm_run_writes_the_marker_only_for_a_database_with_migration_history()
    {
        SkipUnlessUp();
        await _s.UpgradeAsync();
        await PrepareVolumesAsync();
        var expected = (await _s.Guard.ExecAsync(["sh", "-c", "sha256sum /old/global/pg_control | cut -d' ' -f1"])).Stdout.Trim();

        // Before the confirmation the normal run refuses; the confirm run against the restored 18 server then writes the
        // 17 cluster's fingerprint, and the normal run passes.
        Assert.Equal(1, (await GuardAsync()).Exit);
        var confirm = await GuardAsync("WHISKERS_CONFIRM_RESTORE=1");
        Assert.True(confirm.Exit == 0, confirm.Err);
        Assert.Contains("restore confirmed", confirm.Err);
        Assert.Equal(expected, await MarkerAsync());
        Assert.Equal(0, (await GuardAsync()).Exit);

        // 17 started again (a rollback): pg_control changes, the normal run refuses the older 18 copy.
        await _s.Guard.ExecAsync(["sh", "-c", "head -c 8192 /dev/urandom > /old/global/pg_control"]);
        Assert.Equal(1, (await GuardAsync()).Exit);
    }

    [SkippableFact]
    public async Task The_confirm_run_refuses_an_empty_database_and_writes_nothing()
    {
        SkipUnlessUp();
        await _s.UpgradeAsync();
        var create = await _s.Pg18.ExecAsync(["sh", "-c",
            "psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -c 'create database emptydb' -c 'create database nohistory'"
            + " && psql -U \"$POSTGRES_USER\" -d nohistory -v ON_ERROR_STOP=1 -c 'create table \"__EFMigrationsHistory\" (\"MigrationId\" text primary key, \"ProductVersion\" text not null)'"]);
        Assert.True(create.ExitCode == 0, create.Stderr);

        foreach (var database in new[] { "emptydb", "nohistory" })
        {
            await PrepareVolumesAsync();
            var confirm = await GuardAsync("WHISKERS_CONFIRM_RESTORE=1", $"POSTGRES_DB={database}");
            Assert.True(confirm.Exit == 1, $"{database}: {confirm.Err}");
            Assert.Contains("nothing confirmed", confirm.Err);
            Assert.Null(await MarkerAsync());
            Assert.Equal(1, (await GuardAsync()).Exit);
        }

        // No 17 data at all: there is nothing to confirm either.
        var reset = await _s.Guard.ExecAsync(["sh", "-c", "rm -rf /old /new && mkdir /old /new"]);
        Assert.Equal(0, reset.ExitCode);
        Assert.Equal(1, (await GuardAsync("WHISKERS_CONFIRM_RESTORE=1")).Exit);
        Assert.Null(await MarkerAsync());
    }
}
