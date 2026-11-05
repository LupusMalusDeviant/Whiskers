using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// The upgrade path of docs/operations/postgres-18-upgrade.md, end to end with real servers and the guard script from
/// the compose file. A postgres:17-alpine server on its own volume gets Whiskers' schema (the PostgreSQL migrations,
/// applied as at a start) and rows; the guide's own dump command (taken from the guide) refuses while a client is
/// connected and dumps once none is; the fingerprint mode refuses while 17 runs; 17 is stopped; the guard's fingerprint
/// mode reads the stopped cluster through a read-only mount of that volume and its line is appended to the dump; the guard's
/// restore mode restores it into postgres:18-alpine (network alias <c>db</c>) and writes the marker. Afterwards the
/// migration history and every table's row count are equal, the sequences continue and the normal run passes; a 17
/// start makes it refuse, and the same dump is refused as stale. The restore mode's server checks are covered too: a
/// Whiskers-initialised 18 database, a server older than 18, a dump without migration history, a dump that fails
/// halfway (rolled back completely, then a valid one goes through) and a marker that cannot be written.
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresUpgradeTests : IClassFixture<PostgresUpgradeTests.Servers>
{
    private readonly Servers _s;

    public PostgresUpgradeTests(Servers servers) => _s = servers;

    public sealed class Servers : IAsyncLifetime
    {
        public IVolume Volume17 { get; } = new VolumeBuilder().Build();
        public INetwork Network { get; } = new NetworkBuilder().Build();
        public INetwork Network17 { get; } = new NetworkBuilder().Build();
        public PostgreSqlContainer Pg17 { get; }
        public PostgreSqlContainer Pg18 { get; }
        public PostgreSqlContainer Pg17AsDb { get; }
        /// <summary>The guard as the overlay runs it, with the 17 volume read-only at /old.</summary>
        public IContainer Guard { get; }
        /// <summary>A guard on the same network with a prepared /old instead of the volume.</summary>
        public IContainer GuardFake { get; }
        /// <summary>A guard whose host <c>db</c> is a PostgreSQL 17 server.</summary>
        public IContainer GuardOld { get; }
        public Exception? StartFailure { get; private set; }

        private ContainerBuilder GuardBuilder(INetwork network) => new ContainerBuilder("postgres:18-alpine")
            .WithNetwork(network)
            .WithEntrypoint("tail", "-f", "/dev/null")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithEnvironment("PGPASSWORD", "postgres");

        public Servers()
        {
            Pg17 = new PostgreSqlBuilder("postgres:17-alpine").WithVolumeMount(Volume17, "/var/lib/postgresql/data").Build();
            Pg18 = new PostgreSqlBuilder("postgres:18-alpine").WithNetwork(Network).WithNetworkAliases("db").Build();
            Pg17AsDb = new PostgreSqlBuilder("postgres:17-alpine").WithNetwork(Network17).WithNetworkAliases("db").Build();
            Guard = GuardBuilder(Network).WithVolumeMount(Volume17, "/old", AccessMode.ReadOnly).Build();
            GuardFake = GuardBuilder(Network).Build();
            GuardOld = GuardBuilder(Network17).Build();
        }

        public async Task InitializeAsync()
        {
            try
            {
                await Volume17.CreateAsync();
                await Network.CreateAsync();
                await Network17.CreateAsync();
            }
            catch (Exception ex)
            {
                StartFailure = ex;
                return;
            }
            foreach (var c in new IContainer[] { Pg17, Pg18, Pg17AsDb, Guard, GuardFake, GuardOld })
            {
                StartFailure = await DockerRequirement.TryStartAsync(c);
                if (StartFailure is not null) return;
            }
            var mkdir = await Guard.ExecAsync(["mkdir", "-p", "/new"]);
            if (mkdir.ExitCode != 0) StartFailure = new InvalidOperationException(mkdir.Stderr);
        }

        public async Task DisposeAsync()
        {
            foreach (var c in new IContainer[] { GuardOld, GuardFake, Guard, Pg17AsDb, Pg18, Pg17 })
                await c.DisposeAsync();
            await Network17.DisposeAsync();
            await Network.DisposeAsync();
            await Volume17.DisposeAsync();
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

    private static DbContextOptions<MetricsDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsAssembly("Whiskers.Migrations.Postgres"))
            .Options;

    private static async Task MigrateAsync(string connectionString)
    {
        await using var db = new MetricsDbContext(Options(connectionString));
        await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
    }

    private static async Task CreateDatabaseAsync(PostgreSqlContainer server, string name)
    {
        var create = await server.ExecAsync(["sh", "-c",
            $"psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -c 'create database {name}'"]);
        Assert.True(create.ExitCode == 0, create.Stderr);
    }

    private const string Footer = "--\n-- PostgreSQL database dump complete\n--\n";

    /// <summary>A prepared 17 cluster in the fake guard and a dump text bound to it (footer and fingerprint line).</summary>
    private static async Task<string> PrepareFakeAsync(IContainer guard, string body)
    {
        await GuardRunner.ResetAsync(guard, GuardRunner.Cluster("/old"));
        var fp = await GuardRunner.Sha256Async(guard, "/old/global/pg_control");
        await GuardRunner.WriteAsync(guard, "/tmp/in.sql", $"{body}{Footer}{GuardRunner.Tag}{fp}\n");
        return fp;
    }

    private static async Task WaitForNoClientsAsync(PostgreSqlContainer server)
    {
        const string count = "psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -tAc \"select count(*) from pg_stat_activity where datname = current_database() and pid <> pg_backend_pid() and backend_type = 'client backend'\"";
        for (var i = 0; i < 50; i++)
        {
            var run = await server.ExecAsync(["sh", "-c", count]);
            if (run.ExitCode == 0 && run.Stdout.Trim() == "0") return;
            await Task.Delay(200);
        }
        Assert.Fail("the test's own pooled connections to PostgreSQL 17 did not close within 10 s");
    }

    [SkippableFact]
    public async Task The_restore_mode_rolls_a_failing_dump_back_completely_and_a_valid_one_then_goes_through()
    {
        SkipUnlessUp();
        // Review round 3, m2: a dump that fails halfway leaves nothing behind.
        await CreateDatabaseAsync(_s.Pg18, "halfway");
        var halfway = new NpgsqlConnectionStringBuilder(_s.Pg18.GetConnectionString()) { Database = "halfway" }.ConnectionString;
        await PrepareFakeAsync(_s.GuardFake, "CREATE TABLE half (x int);\nINSERT INTO half VALUES (1);\nSELECT 1/0;\n");

        var failed = await GuardRunner.RunAsync(_s.GuardFake, "/tmp/in.sql", "WHISKERS_RESTORE=1", "POSTGRES_DB=halfway");

        Assert.True(failed.Exit == 1, failed.Err);
        Assert.Contains("the restore failed and was rolled back; nothing restored", failed.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_s.GuardFake));
        await using (var conn = new NpgsqlConnection(halfway))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("select to_regclass('public.half') is null", conn);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
        }

        // The same database, a valid dump: it goes through, as the guide says ("run the same command again").
        var fp = await PrepareFakeAsync(_s.GuardFake,
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);\n"
            + "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20260101000000_Init', '10.0.14');\n");
        var ok = await GuardRunner.RunAsync(_s.GuardFake, "/tmp/in.sql", "WHISKERS_RESTORE=1", "POSTGRES_DB=halfway");
        Assert.True(ok.Exit == 0, ok.Err);
        Assert.Equal(fp, await GuardRunner.MarkerAsync(_s.GuardFake));
    }

    [SkippableFact]
    public async Task The_restore_mode_reports_a_marker_it_could_not_write()
    {
        SkipUnlessUp();
        await CreateDatabaseAsync(_s.Pg18, "nomarker");
        await PrepareFakeAsync(_s.GuardFake,
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);\n"
            + "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20260101000000_Init', '10.0.14');\n");
        // /new unusable as a directory (root ignores permissions, so a file stands in for a read-only volume).
        var block = await _s.GuardFake.ExecAsync(["sh", "-c", "rm -rf /new && touch /new"]);
        Assert.Equal(0, block.ExitCode);

        var run = await GuardRunner.RunAsync(_s.GuardFake, "/tmp/in.sql", "WHISKERS_RESTORE=1", "POSTGRES_DB=nomarker");

        Assert.True(run.Exit == 1, run.Err);
        Assert.Contains("the dump was restored, but the marker could not be written into whiskers-db18-data", run.Err);
        Assert.Contains("Remove the 18 volume", run.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_s.GuardFake));
    }

    [SkippableFact]
    public async Task The_guides_flow_carries_a_17_install_into_18_and_the_guard_follows_the_17_state()
    {
        SkipUnlessUp();
        // A 17 install with Whiskers' schema and rows.
        await MigrateAsync(_s.Pg17.GetConnectionString());
        await using (var db = new MetricsDbContext(Options(_s.Pg17.GetConnectionString())))
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
                db.AuditLog.Add(new AuditLogEntity { Actor = $"user{i}@example.com", ActorType = "web", Action = "container.restart",
                    TargetType = "container", TargetId = $"c{i}", Details = "Ärger <&>", Timestamp = now.AddMinutes(-i) });
            db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ContainerName = "web", ServerId = "s1",
                Timestamp = now, CpuPercent = 3.5 });
            db.Notifications.Add(new NotificationEntity { NotificationId = "n1", Title = "t", Detail = "d", Timestamp = now,
                Severity = "Info", Read = false });
            await db.SaveChangesAsync();
        }
        var before = await Snapshot.TakeAsync(_s.Pg17.GetConnectionString());
        Assert.NotEmpty(before.History);

        // A blind pull: the normal run refuses.
        Assert.Equal(1, (await GuardRunner.RunAsync(_s.Guard)).Exit);

        // Step 1, the guide's own dump command (c17 exec -T db sh -c '…' > whiskers-pg17.sql). With a client still
        // connected (Whiskers not stopped) it refuses and writes nothing to stdout, so the file stays empty and has no
        // closing line for the restore to accept.
        var dumpScript = PostgresOverlayTests.GuideDumpScript();
        await using (var whiskers = new NpgsqlConnection(_s.Pg17.GetConnectionString()))
        {
            await whiskers.OpenAsync();
            var refused = await _s.Pg17.ExecAsync(["sh", "-c", dumpScript]);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Equal("", refused.Stdout);
            Assert.Contains("stop Whiskers first: c17 stop serverwatch", refused.Stderr);
        }
        // Whiskers stopped: no client left (the pooled connections closed).
        NpgsqlConnection.ClearAllPools();
        await WaitForNoClientsAsync(_s.Pg17);
        var dump = await _s.Pg17.ExecAsync(["sh", "-c", dumpScript]);
        Assert.True(dump.ExitCode == 0, dump.Stderr);
        Assert.Contains("PostgreSQL database dump complete", dump.Stdout);

        // c17 stop db; c run --rm --no-deps -T -e WHISKERS_FINGERPRINT=1 db-upgrade-guard >> whiskers-pg17.sql
        // Before the stop the fingerprint mode refuses: a running 17 rewrites pg_control when it stops.
        var early = await GuardRunner.RunAsync(_s.Guard, null, "WHISKERS_FINGERPRINT=1");
        Assert.Equal(1, early.Exit);
        Assert.Equal("", early.Out);
        Assert.Contains("Stop PostgreSQL 17 first: c17 stop db.", early.Err);
        await _s.Pg17.StopAsync();
        var fingerprint = await GuardRunner.RunAsync(_s.Guard, null, "WHISKERS_FINGERPRINT=1");
        Assert.True(fingerprint.Exit == 0, fingerprint.Err);
        var fp = await GuardRunner.Sha256Async(_s.Guard, "/old/global/pg_control");
        Assert.Equal($"{GuardRunner.Tag}{fp}\n", fingerprint.Out);
        await GuardRunner.WriteAsync(_s.Guard, "/tmp/in.sql", dump.Stdout + fingerprint.Out);

        // Step 2: c run --rm --no-deps -T -e WHISKERS_RESTORE=1 db-upgrade-guard < whiskers-pg17.sql
        var restore = await GuardRunner.RunAsync(_s.Guard, "/tmp/in.sql", "WHISKERS_RESTORE=1");
        Assert.True(restore.Exit == 0, restore.Err);
        Assert.Contains("whiskers: restored:", restore.Err);
        Assert.Equal(fp, await GuardRunner.MarkerAsync(_s.Guard));

        var after = await Snapshot.TakeAsync(_s.Pg18.GetConnectionString());
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Rows, after.Rows);
        Assert.Equal(5, after.Rows["public.\"AuditLog\""]);
        Assert.Equal(before.Sequences.Keys, after.Sequences.Keys);
        await using (var conn = new NpgsqlConnection(_s.Pg18.GetConnectionString()))
        {
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

        // Step 3: the normal run passes.
        var start = await GuardRunner.RunAsync(_s.Guard);
        Assert.True(start.Exit == 0, start.Err);

        // The same restore again: the 18 database holds Whiskers' tables now.
        var again = await GuardRunner.RunAsync(_s.Guard, "/tmp/in.sql", "WHISKERS_RESTORE=1");
        Assert.Equal(1, again.Exit);
        Assert.Contains("already holds Whiskers' tables", again.Err);

        // Going back: 17 starts and stops again. The normal run refuses the older 18 copy, and the old dump is stale.
        await _s.Pg17.StartAsync();
        await _s.Pg17.StopAsync();
        Assert.NotEqual(fp, await GuardRunner.Sha256Async(_s.Guard, "/old/global/pg_control"));
        var rollback = await GuardRunner.RunAsync(_s.Guard);
        Assert.Equal(1, rollback.Exit);
        Assert.Contains("the PostgreSQL 17 data changed after the upgrade", rollback.Err);
        var stale = await GuardRunner.RunAsync(_s.Guard, "/tmp/in.sql", "WHISKERS_RESTORE=1");
        Assert.Equal(1, stale.Exit);
        Assert.Contains("the dump is not of the current PostgreSQL 17 data", stale.Err);
        Assert.Equal(fp, await GuardRunner.MarkerAsync(_s.Guard));   // untouched
    }

    [SkippableFact]
    public async Task The_restore_mode_refuses_an_18_database_whiskers_initialised_itself()
    {
        SkipUnlessUp();
        // Review M1, scenario A: db started with --no-deps, then Whiskers migrated the empty 18 database on its own.
        await CreateDatabaseAsync(_s.Pg18, "selfinit");
        var selfInit = new NpgsqlConnectionStringBuilder(_s.Pg18.GetConnectionString()) { Database = "selfinit" }.ConnectionString;
        await MigrateAsync(selfInit);
        var history = (await Snapshot.TakeAsync(selfInit)).History;
        await PrepareFakeAsync(_s.GuardFake, "SELECT 1;\n");

        var run = await GuardRunner.RunAsync(_s.GuardFake, "/tmp/in.sql", "WHISKERS_RESTORE=1", "POSTGRES_DB=selfinit");

        Assert.True(run.Exit == 1, run.Err);
        Assert.Contains("the PostgreSQL 18 database already holds Whiskers' tables (did Whiskers start on it?)", run.Err);
        Assert.Contains("nothing restored", run.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_s.GuardFake));
        Assert.Equal(history, (await Snapshot.TakeAsync(selfInit)).History);
        Assert.Equal(1, (await GuardRunner.RunAsync(_s.GuardFake)).Exit);
    }

    [SkippableFact]
    public async Task The_restore_mode_refuses_a_server_older_than_18()
    {
        SkipUnlessUp();
        // Review m2: the 17 db still up under the name db.
        await PrepareFakeAsync(_s.GuardOld, "SELECT 1;\n");

        var run = await GuardRunner.RunAsync(_s.GuardOld, "/tmp/in.sql", "WHISKERS_RESTORE=1");

        Assert.True(run.Exit == 1, run.Err);
        Assert.Contains("not 18", run.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_s.GuardOld));
    }

    [SkippableFact]
    public async Task The_restore_mode_writes_no_marker_for_a_dump_without_migration_history()
    {
        SkipUnlessUp();
        await CreateDatabaseAsync(_s.Pg18, "blank");
        await PrepareFakeAsync(_s.GuardFake, "CREATE TABLE notwhiskers (x int);\n");

        var run = await GuardRunner.RunAsync(_s.GuardFake, "/tmp/in.sql", "WHISKERS_RESTORE=1", "POSTGRES_DB=blank");

        Assert.True(run.Exit == 1, run.Err);
        Assert.Contains("holds no Whiskers migration history; not marking it", run.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_s.GuardFake));
    }
}

/// <summary>
/// The property the guard rests on (review round 2, m3): a real PostgreSQL 17 start rewrites global/pg_control, so its
/// fingerprint changes, while a stopped cluster keeps it. Measured with postgres:17-alpine on one volume, read by the
/// guard's fingerprint mode through a read-only mount.
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresPgControlTests : IAsyncLifetime
{
    private readonly IVolume _volume = new VolumeBuilder().Build();
    private readonly PostgreSqlContainer _pg17;
    private readonly IContainer _reader;
    private Exception? _startFailure;

    public PostgresPgControlTests()
    {
        _pg17 = new PostgreSqlBuilder("postgres:17-alpine").WithVolumeMount(_volume, "/var/lib/postgresql/data").Build();
        _reader = new ContainerBuilder("postgres:18-alpine")
            .WithEntrypoint("tail", "-f", "/dev/null")
            .WithVolumeMount(_volume, "/old", AccessMode.ReadOnly)
            .Build();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _volume.CreateAsync();
        }
        catch (Exception ex)
        {
            _startFailure = ex;
            return;
        }
        _startFailure = await DockerRequirement.TryStartAsync(_pg17) ?? await DockerRequirement.TryStartAsync(_reader);
    }

    public async Task DisposeAsync()
    {
        await _reader.DisposeAsync();
        await _pg17.DisposeAsync();
        await _volume.DisposeAsync();
    }

    private async Task<string> FingerprintAsync()
    {
        var run = await GuardRunner.RunAsync(_reader, null, "WHISKERS_FINGERPRINT=1");
        Assert.True(run.Exit == 0, run.Err);
        Assert.StartsWith(GuardRunner.Tag, run.Out);
        return run.Out.Trim();
    }

    [SkippableFact]
    public async Task A_postgresql_17_start_changes_the_fingerprint_and_a_stopped_cluster_keeps_it()
    {
        DockerRequirement.SkipUnlessStarted(_startFailure, "The PostgreSQL 17 server");
        await _pg17.StopAsync();
        var first = await FingerprintAsync();
        Assert.Equal(first, await FingerprintAsync());

        await _pg17.StartAsync();
        await _pg17.StopAsync();
        var second = await FingerprintAsync();

        Assert.NotEqual(first, second);
        Assert.Equal(second, await FingerprintAsync());
    }
}
