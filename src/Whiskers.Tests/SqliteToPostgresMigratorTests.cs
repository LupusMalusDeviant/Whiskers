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

    [Theory]
    [InlineData("DROP TABLE Operations; DROP TABLE Approvals; DROP TABLE PolicyDecisions; DROP TABLE PolicySnapshots;", "PolicyDecisions")]  // before R1-12
    [InlineData("DROP TABLE Operations; DROP TABLE Approvals;", "Approvals")]                                        // before R1-14
    [InlineData("DROP TABLE Operations;", "Operations")]                                                             // before R1-18
    public async Task A_source_from_an_older_release_migrates_completely(string drop, string absent)
    {
        // A database migrated by an older release lacks the tables of every release after its own.
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        await using (var src = NewDb(srcPath))
        {
            src.ScheduledTasks.Add(new ScheduledTaskEntity { TaskId = "task-abc", Name = "nightly", CronExpression = "0 2 * * *",
                TaskType = ScheduledTaskType.DbBackup, CreatedBy = "admin" });
            await src.SaveChangesAsync();
            await src.Database.ExecuteSqlRawAsync(drop);
        }

        int rc;
        var log = new StringWriter();
        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, log);

        Assert.Equal(0, rc);
        Assert.Contains($"no '{absent}' table", log.ToString());
        await using var check = NewDb(dstPath);
        Assert.Equal(1, await check.ScheduledTasks.CountAsync());
        Assert.Equal(0, await check.PolicyDecisions.CountAsync());
        Assert.Equal(0, await check.Approvals.CountAsync());
    }

    [Theory]
    [InlineData("DROP TABLE PolicySnapshots;")]                       // a decision table without its versions
    [InlineData("DROP TABLE PolicyDecisions; DROP TABLE PolicySnapshots;")]   // R1-12's tables gone, R1-14's there
    [InlineData("DROP TABLE ScheduledTasks;")]                        // a table every supported release has
    public async Task A_source_missing_other_tables_is_refused_before_anything_is_copied(string drop)
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        await using (var src = NewDb(srcPath))
        {
            src.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ContainerName = "web", ServerId = "s1",
                Timestamp = DateTime.UtcNow });
            await src.SaveChangesAsync();
            await src.Database.ExecuteSqlRawAsync(drop);
        }

        int rc;
        var log = new StringWriter();
        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, log);

        Assert.NotEqual(0, rc);
        Assert.Contains("not a complete Whiskers database", log.ToString());
        await using var check = NewDb(dstPath);
        Assert.Equal(0, await check.ContainerMetrics.CountAsync());   // nothing copied
    }

    [Fact]
    public async Task An_empty_file_is_refused_not_migrated()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        int rc;
        var log = new StringWriter();
        await using (var src = new MetricsDbContext(
                         new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={srcPath}").Options))
        await using (var dst = NewDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, log);

        Assert.NotEqual(0, rc);
        Assert.Contains("not a complete Whiskers database", log.ToString());
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

    [Fact]
    public void Every_entity_of_the_model_is_copied()
    {
        // A table added to MetricsDbContext without a line in the migrator is silent data loss on the move to
        // PostgreSQL. It happened once (UpdateRollbacks, SelfMetricSamples, ActionOutcomes) — this pins it.
        using var ctx = NewDb(NewDbPath());
        var modelled = ctx.Model.GetEntityTypes().Where(e => !e.IsOwned()).Select(e => e.ClrType).ToHashSet();
        var copied = SqliteToPostgresMigrator.MetricsTables.Select(t => t.EntityType).ToHashSet();

        Assert.Empty(modelled.Except(copied).Select(t => t.Name));
        Assert.Empty(copied.Except(modelled).Select(t => t.Name));
    }

    [Fact]
    public async Task Copies_the_tables_added_after_the_first_release()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        await using (var src = NewDb(srcPath))
        {
            src.UpdateRollbacks.Add(new UpdateRollbackEntity { Id = 5, ContainerId = "c1", ContainerName = "web", OldImageRef = "sha256:old", CapturedAt = DateTime.UtcNow });
            src.SelfMetricSamples.Add(new SelfMetricSampleEntity { Id = 6, ServerId = "s1" });
            src.ActionOutcomes.Add(new ActionOutcomeEntity { Id = 7, CorrelationId = "corr-1", ServerId = "s1" });
            await src.SaveChangesAsync();
        }

        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
            Assert.Equal(0, await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, TextWriter.Null));

        await using var check = NewDb(dstPath);
        Assert.Equal("sha256:old", (await check.UpdateRollbacks.SingleAsync()).OldImageRef);
        Assert.Equal(1, await check.SelfMetricSamples.CountAsync());
        Assert.Equal("corr-1", (await check.ActionOutcomes.SingleAsync()).CorrelationId);
    }

    private static WhiskersIdentityDbContext NewIdentityDb(string path)
    {
        // Metrics tables first: EnsureCreated does nothing on a file that already has (identity) tables.
        using (NewDb(path)) { }
        var ctx = new WhiskersIdentityDbContext(
            new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
                .UseSqlite($"Data Source={path}", o =>
                {
                    o.MigrationsAssembly("Whiskers.Migrations.Sqlite");
                    o.MigrationsHistoryTable("__IdentityMigrationsHistory");
                }).Options);
        ctx.Database.Migrate();
        return ctx;
    }

    [Fact]
    public async Task Copies_local_users_with_their_ids_and_password_hashes()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        await using (var id = NewIdentityDb(srcPath))
        {
            id.Users.Add(new AppUser { Id = "user-1", UserName = "admin@example.org", NormalizedUserName = "ADMIN@EXAMPLE.ORG", Email = "admin@example.org", PasswordHash = "HASH" });
            id.UserClaims.Add(new Microsoft.AspNetCore.Identity.IdentityUserClaim<string> { Id = 9, UserId = "user-1", ClaimType = "c", ClaimValue = "v" });
            await id.SaveChangesAsync();
        }

        int rc;
        await using (var src = NewDb(srcPath))
        await using (var dst = NewDb(dstPath))
        await using (var idSrc = NewIdentityDb(srcPath))
        await using (var idDst = NewIdentityDb(dstPath))
            rc = await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, TextWriter.Null, default, idSrc, idDst);

        Assert.Equal(0, rc);
        await using var check = NewIdentityDb(dstPath);
        var user = await check.Users.SingleAsync();
        Assert.Equal("user-1", user.Id);
        Assert.Equal("HASH", user.PasswordHash);
        Assert.Equal("user-1", (await check.UserClaims.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Aborts_when_the_target_already_has_local_users()
    {
        var srcPath = NewDbPath();
        var dstPath = NewDbPath();
        await using (var id = NewIdentityDb(dstPath))
        {
            id.Users.Add(new AppUser { Id = "seeded", UserName = "x", NormalizedUserName = "X" });
            await id.SaveChangesAsync();
        }

        await using var src = NewDb(srcPath);
        await using var dst = NewDb(dstPath);
        await using var idSrc = NewIdentityDb(srcPath);
        await using var idDst = NewIdentityDb(dstPath);
        Assert.Equal(3, await SqliteToPostgresMigrator.MigrateDataAsync(src, dst, TextWriter.Null, default, idSrc, idDst));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }
}
