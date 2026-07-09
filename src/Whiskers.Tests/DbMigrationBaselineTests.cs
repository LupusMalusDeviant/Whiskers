using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>Data-safety proof for the EF Core migrations adoption (ADR-0003 / MIT-27, MIT-29).
/// Every case runs against a real temp-file SQLite database — an in-memory DB cannot exercise the
/// legacy-baseline path faithfully (connection reuse, migration history table, file semantics).</summary>
public sealed class DbMigrationBaselineTests : IDisposable
{
    private readonly List<string> _files = new();
    private readonly List<string> _dirs = new();

    private string NewDbPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sw-mig-{Guid.NewGuid():N}.db");
        _files.Add(p);
        return p;
    }

    // The context lives in Whiskers.Data but its migrations stay in the Whiskers assembly (ADR-0004),
    // so — exactly like production (DatabaseRegistration / MetricsDbContextFactory) — the test must point
    // EF at that migrations assembly, otherwise MigrateAsync/GetMigrations find nothing.
    private static MetricsDbContext Ctx(string dataSource) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={dataSource}", sql => sql.MigrationsAssembly("Whiskers")).Options);

    // Seeds a database shaped like a pre-migration deployment: a handful of real tables with rows,
    // deliberately WITHOUT AlertHistory and WITHOUT __EFMigrationsHistory (the legacy EnsureCreated state).
    private static async Task SeedLegacyAsync(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "ContainerMetrics" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ContainerId" TEXT NOT NULL, "ContainerName" TEXT NOT NULL, "ServerId" TEXT NOT NULL, "Timestamp" TEXT NOT NULL, "CpuPercent" REAL NOT NULL, "MemoryUsageBytes" INTEGER NOT NULL, "MemoryLimitBytes" INTEGER NOT NULL, "NetworkRxBytes" INTEGER NOT NULL, "NetworkTxBytes" INTEGER NOT NULL, "BlockReadBytes" INTEGER NOT NULL, "BlockWriteBytes" INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS "AuditLog" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Timestamp" TEXT NOT NULL, "Actor" TEXT NOT NULL, "ActorType" TEXT NOT NULL, "Action" TEXT NOT NULL, "TargetType" TEXT NOT NULL, "TargetId" TEXT NOT NULL, "TargetName" TEXT NOT NULL, "Details" TEXT, "ServerId" TEXT, "Success" INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS "McpToolCalls" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Timestamp" TEXT NOT NULL, "Actor" TEXT NOT NULL, "ActorType" TEXT NOT NULL, "ToolName" TEXT NOT NULL, "Level" TEXT NOT NULL, "ParamsJson" TEXT, "Verdict" TEXT NOT NULL, "Success" INTEGER NOT NULL, "DurationMs" INTEGER NOT NULL, "ResultSummary" TEXT, "ServerId" TEXT, "Error" TEXT);
            INSERT INTO "ContainerMetrics" ("ContainerId","ContainerName","ServerId","Timestamp","CpuPercent","MemoryUsageBytes","MemoryLimitBytes","NetworkRxBytes","NetworkTxBytes","BlockReadBytes","BlockWriteBytes") VALUES ('c1','web','local','2026-01-01T00:00:00',1.5,100,200,0,0,0,0);
            INSERT INTO "AuditLog" ("Timestamp","Actor","ActorType","Action","TargetType","TargetId","TargetName","Success") VALUES ('2026-01-01T00:00:00','admin','user','login','system','','',1);
            INSERT INTO "McpToolCalls" ("Timestamp","Actor","ActorType","ToolName","Level","Verdict","Success","DurationMs") VALUES ('2026-01-01T00:00:00','key','mcp','list_containers','ReadOnly','Allowed',1,5);
            """;
        await cmd.ExecuteNonQueryAsync();
        conn.Close();
        SqliteConnection.ClearAllPools(); // release the seed file handle before the initializer opens the DB
    }

    [Fact]
    public async Task LegacyDb_Baselines_NoDataLoss()
    {
        var path = NewDbPath();
        await SeedLegacyAsync(path);

        await using (var db = Ctx(path))
            await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);

        await using var check = Ctx(path);
        // 1. No data loss — every seeded row survived the baseline.
        Assert.Equal(1, await check.ContainerMetrics.CountAsync());
        Assert.Equal(1, await check.AuditLog.CountAsync());
        Assert.Equal(1, await check.McpToolCalls.CountAsync());
        // 2. The table the old DDL forgot now exists and is prunable (the MIT-27 regression).
        await check.AlertHistory.Where(a => a.Timestamp < new DateTime(2026, 1, 1)).ExecuteDeleteAsync();
        // 3. The database is now on migrations.
        var applied = await check.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("InitialCreate"));
    }

    [Fact]
    public async Task FreshDb_CreatesFullSchema()
    {
        var path = NewDbPath(); // file does not exist yet

        await using (var db = Ctx(path))
            await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);

        // DDL-vs-entity consistency: every DbSet must be queryable. A missing table throws here — this is
        // the test that would have caught MIT-27 mechanically.
        await using var db2 = Ctx(path);
        Assert.Equal(0, await db2.ContainerMetrics.CountAsync());
        Assert.Equal(0, await db2.ServerMetrics.CountAsync());
        Assert.Equal(0, await db2.AlertHistory.CountAsync());
        Assert.Equal(0, await db2.AuditLog.CountAsync());
        Assert.Equal(0, await db2.McpToolCalls.CountAsync());
        Assert.Equal(0, await db2.VolumeBackups.CountAsync());
        Assert.Equal(0, await db2.ScheduledTasks.CountAsync());
        Assert.Equal(0, await db2.TaskRunHistory.CountAsync());
        Assert.Equal(0, await db2.LogAlertRules.CountAsync());
        Assert.Equal(0, await db2.UpdatePolicies.CountAsync());
        Assert.Equal(0, await db2.UpdateHistory.CountAsync());
        Assert.Equal(0, await db2.Webhooks.CountAsync());
        Assert.Equal(0, await db2.WebhookLogs.CountAsync());
        Assert.Equal(0, await db2.CveFirstSeen.CountAsync());
        Assert.Equal(0, await db2.Notifications.CountAsync());

        var applied = await db2.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("InitialCreate"));
    }

    [Fact]
    public async Task AfterInit_JournalModeIsWal()
    {
        var path = NewDbPath();

        await using (var db = Ctx(path))
            await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);

        // WAL persists in the database header, so a fresh connection sees it too.
        await using var check = Ctx(path);
        var conn = check.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task SecondRun_Idempotent()
    {
        var path = NewDbPath();
        await SeedLegacyAsync(path);

        await using (var db1 = Ctx(path))
            await DatabaseInitializer.InitializeAsync(db1, NullLogger.Instance);
        // A second startup must not throw and must not re-baseline.
        await using (var db2 = Ctx(path))
            await DatabaseInitializer.InitializeAsync(db2, NullLogger.Instance);

        await using var check = Ctx(path);
        Assert.Equal(1, await check.ContainerMetrics.CountAsync()); // rows unchanged
        var applied = await check.Database.GetAppliedMigrationsAsync();
        Assert.Single(applied); // InitialCreate recorded exactly once
    }

    [Fact]
    public async Task BadDataSource_ThrowsClearly_NoSecretLeak()
    {
        // Point the connection at a directory so SQLite cannot open it — a stand-in for an unwritable/corrupt DB.
        var dir = Path.Combine(Path.GetTempPath(), $"sw-mig-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        await using var db = Ctx(dir);
        var log = new CapturingLogger();

        // Startup fails fast with a surfaced exception — never a silently swallowed crash loop.
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.InitializeAsync(db, log));

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical);
        // SQLite connection strings carry only a file path (no credential), but we still assert the failure
        // path leaks nothing sensitive — the contract holds regardless of provider.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
        foreach (var d in _dirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp dir */ }
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Ex)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
