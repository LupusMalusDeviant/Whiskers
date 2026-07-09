using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>Data-safety proof for the one-time SQLite → target data copy (stableDB step 7). Two temp-file
/// SQLite databases stand in for source + target, so the provider-agnostic core (`MigrateDataAsync`) is
/// exercised without a real PostgreSQL server.</summary>
public sealed class SqliteToPostgresMigratorTests : IDisposable
{
    private readonly List<string> _files = new();

    private string NewDbPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sw-mig7-{Guid.NewGuid():N}.db");
        _files.Add(p);
        return p;
    }

    private static MetricsDbContext NewDb(string path)
    {
        var ctx = new MetricsDbContext(
            new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={path}").Options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task Copies_all_rows_reassigns_ids_preserves_business_keys_and_leaves_source_intact()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();

        // Seed the source with rows carrying explicit (non-1) surrogate Ids + meaningful business keys.
        await using (var src = NewDb(srcPath))
        {
            src.ContainerMetrics.Add(new ContainerMetricEntity { Id = 41, ContainerId = "c1", ContainerName = "web", ServerId = "s1", Timestamp = DateTime.UtcNow, CpuPercent = 1.5 });
            src.ContainerMetrics.Add(new ContainerMetricEntity { Id = 42, ContainerId = "c2", ContainerName = "db", ServerId = "s1", Timestamp = DateTime.UtcNow, CpuPercent = 2.5 });
            src.ScheduledTasks.Add(new ScheduledTaskEntity { Id = 7, TaskId = "task-abc", Name = "nightly", CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.DbBackup, CreatedBy = "admin" });
            src.CveFirstSeen.Add(new CveFirstSeenEntity { Id = 99, IdentityKey = "srv|trivy|c1|pkg|CVE-2026-1", CveId = "CVE-2026-1", FirstSeenUtc = DateTime.UtcNow });
            await src.SaveChangesAsync();
        }

        int rc;
        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, TextWriter.Null);

        Assert.Equal(0, rc);

        await using (var check = NewDb(dstPath))
        {
            Assert.Equal(2, await check.ContainerMetrics.CountAsync());
            Assert.Equal(1, await check.ScheduledTasks.CountAsync());
            Assert.Equal(1, await check.CveFirstSeen.CountAsync());
            // Business keys survive verbatim.
            Assert.NotNull(await check.ScheduledTasks.SingleAsync(t => t.TaskId == "task-abc"));
            Assert.NotNull(await check.CveFirstSeen.SingleAsync(c => c.IdentityKey == "srv|trivy|c1|pkg|CVE-2026-1"));
            // Surrogate Ids were reassigned by the target (fresh identity 1,2 — not the source's 41,42).
            var ids = await check.ContainerMetrics.Select(e => e.Id).OrderBy(x => x).ToListAsync();
            Assert.Equal(new long[] { 1, 2 }, ids);
        }

        // Source is untouched — same counts, original Ids intact.
        await using (var src = NewDb(srcPath))
        {
            Assert.Equal(2, await src.ContainerMetrics.CountAsync());
            Assert.Contains(await src.ContainerMetrics.Select(e => e.Id).ToListAsync(), id => id == 41);
        }
    }

    [Fact]
    public async Task Aborts_without_copying_when_target_is_not_empty()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();

        await using (var src = NewDb(srcPath))
        {
            src.AuditLog.Add(new AuditLogEntity { Actor = "a", Action = "login" });
            await src.SaveChangesAsync();
        }
        await using (var dst = NewDb(dstPath))
        {
            dst.Notifications.Add(new NotificationEntity { NotificationId = "n1", Title = "t", Detail = "d" });
            await dst.SaveChangesAsync();
        }

        int rc;
        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, TextWriter.Null);

        Assert.Equal(3, rc); // abort: never merge into a populated target
        // Nothing copied — the target keeps only its pre-existing notification, no audit rows leaked in.
        await using (var check = NewDb(dstPath))
        {
            Assert.Equal(0, await check.AuditLog.CountAsync());
            Assert.Equal(1, await check.Notifications.CountAsync());
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }
}
