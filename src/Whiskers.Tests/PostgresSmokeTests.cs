using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>Smoke tests against a REAL PostgreSQL 17 (Testcontainers) — the automated counterpart to the
/// manual Docker-host proof (stableDB step 8). Marked <c>RequiresDocker</c> and <b>skipped</b> when no Docker
/// daemon is reachable, so <c>dotnet test</c> stays green on machines/CI without Docker; run them with Docker
/// up (e.g. <c>dotnet test --filter Category=RequiresDocker</c>).</summary>
[Trait("Category", "RequiresDocker")]
public sealed class PostgresSmokeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private bool _up;

    public async Task InitializeAsync()
    {
        try { await _pg.StartAsync(); _up = true; }
        catch { _up = false; } // no Docker daemon → the tests below Skip instead of failing
    }

    public async Task DisposeAsync()
    {
        if (_up) await _pg.DisposeAsync();
    }

    private MetricsDbContext Pg() =>
        new(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(_pg.GetConnectionString(), o => o.MigrationsAssembly("Whiskers.Migrations.Postgres"))
            .Options);

    [SkippableFact]
    public async Task Migrate_crud_retention_and_markread_on_real_postgres()
    {
        Skip.IfNot(_up, "Docker daemon not available");

        // DatabaseInitializer's PostgreSQL branch brings the schema up (exercises the non-SQLite path).
        await using (var db = Pg())
        {
            await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialCreate"));
        }

        var now = DateTime.UtcNow;
        await using (var db = Pg())
        {
            db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ContainerName = "web", ServerId = "s1", Timestamp = now, CpuPercent = 3.5 });
            db.Notifications.Add(new NotificationEntity { NotificationId = "n1", Title = "t", Detail = "d", Timestamp = now, Severity = "Info", Read = false });
            db.CveFirstSeen.Add(new CveFirstSeenEntity { IdentityKey = "k1", CveId = "CVE-2026-1", FirstSeenUtc = now });
            await db.SaveChangesAsync();
        }

        await using (var db = Pg())
        {
            // Every one of the 15 DbSets is queryable → the schema is complete (a missing table throws here).
            Assert.Equal(1, await db.ContainerMetrics.CountAsync());
            Assert.Equal(0, await db.ServerMetrics.CountAsync());
            Assert.Equal(0, await db.AlertHistory.CountAsync());
            Assert.Equal(0, await db.AuditLog.CountAsync());
            Assert.Equal(0, await db.McpToolCalls.CountAsync());
            Assert.Equal(0, await db.VolumeBackups.CountAsync());
            Assert.Equal(0, await db.ScheduledTasks.CountAsync());
            Assert.Equal(0, await db.TaskRunHistory.CountAsync());
            Assert.Equal(0, await db.LogAlertRules.CountAsync());
            Assert.Equal(0, await db.UpdatePolicies.CountAsync());
            Assert.Equal(0, await db.UpdateHistory.CountAsync());
            Assert.Equal(0, await db.Webhooks.CountAsync());
            Assert.Equal(0, await db.WebhookLogs.CountAsync());
            Assert.Equal(1, await db.CveFirstSeen.CountAsync());
            Assert.Equal(1, await db.Notifications.CountAsync());

            // UTC round-trips as UtcKind — the Npgsql timestamptz contract the UtcDateTimeConverter guarantees.
            Assert.Equal(DateTimeKind.Utc, (await db.ContainerMetrics.SingleAsync()).Timestamp.Kind);
        }

        await using (var db = Pg())
        {
            // The two provider-portability-sensitive write paths, on real PostgreSQL:
            await db.ContainerMetrics.Where(m => m.Timestamp < now.AddMinutes(1)).ExecuteDeleteAsync(); // retention prune (timestamptz)
            await db.Notifications.Where(n => !n.Read).ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, true)); // MarkAllRead (boolean)
        }

        await using (var db = Pg())
        {
            Assert.Equal(0, await db.ContainerMetrics.CountAsync());
            Assert.True((await db.Notifications.SingleAsync()).Read);
        }
    }

    [SkippableFact]
    public async Task Migrator_copies_sqlite_data_into_real_postgres()
    {
        Skip.IfNot(_up, "Docker daemon not available");

        var srcPath = Path.Combine(Path.GetTempPath(), $"sw-smoke-{Guid.NewGuid():N}.db");
        try
        {
            // A stand-in SQLite "metrics.db" with rows carrying explicit Ids + business keys.
            await using (var src = Sqlite(srcPath))
            {
                await src.Database.EnsureCreatedAsync();
                src.AuditLog.Add(new AuditLogEntity { Id = 55, Actor = "admin", ActorType = "web", Action = "login", TargetType = "system" });
                src.ScheduledTasks.Add(new ScheduledTaskEntity { Id = 9, TaskId = "task-x", Name = "nightly", CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.DbBackup, CreatedBy = "admin" });
                await src.SaveChangesAsync();
            }

            await using (var tgt = Pg())
                await tgt.Database.MigrateAsync();

            int rc;
            await using (var src = Sqlite(srcPath))
            await using (var tgt = Pg())
                rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, tgt, TextWriter.Null);

            Assert.Equal(0, rc);
            await using (var tgt = Pg())
            {
                Assert.Equal(1, await tgt.AuditLog.CountAsync());
                // Business key survives the copy into real PostgreSQL; the surrogate Id is reassigned (not 9).
                var task = await tgt.ScheduledTasks.SingleAsync(t => t.TaskId == "task-x");
                Assert.NotEqual(9, task.Id);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { srcPath, srcPath + "-wal", srcPath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
        }
    }

    private static MetricsDbContext Sqlite(string path) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={path}").Options);
}
