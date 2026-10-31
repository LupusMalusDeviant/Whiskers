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
    public async Task Approval_decisions_and_uses_commit_on_real_postgres_with_retries_on()
    {
        Skip.IfNot(_up, "Docker daemon not available");
        // R1-15: the production registration enables retries; a decision runs in a transaction inside the strategy.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<MetricsDbContext>(
            services, o => o.UseNpgsql(_pg.GetConnectionString(), npg =>
            {
                npg.EnableRetryOnFailure(3);
                npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
            }), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient);
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        await using (var db = Pg()) await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);

        var persistence = new Whiskers.Services.Approvals.DbApprovalPersistence(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp));
        var store = new Whiskers.Services.Approvals.ApprovalStore(persistence: persistence);
        await store.LoadAsync();
        var approval = await store.CreateAsync(new Whiskers.Models.Agent.ApprovalRequest(
            "s1", "c1", "ops@example.com", "agent-web", "restart_container", McpPermissionLevels.Write, null,
            "needs a human", Binding: "binding-c1", Requesters: ["user:ops@example.com"]), _ => Task.CompletedTask);

        Assert.Equal(Whiskers.Services.Approvals.ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true,
            new Whiskers.Services.Approvals.ApprovalResolver("user:bob@example.com", McpPermissionLevels.Admin, "bob")));
        Assert.True(await store.TryConsumeAsync(approval.Id, "op-1", DateTime.UtcNow));
        // A refused commit leaves nothing behind.
        var second = await store.CreateAsync(new Whiskers.Models.Agent.ApprovalRequest(
            "s1", "c2", "ops@example.com", "agent-web", "restart_container", McpPermissionLevels.Write, null,
            "needs a human", Binding: "binding-c2", Requesters: ["user:ops@example.com"]), _ => Task.CompletedTask);
        Assert.False(await persistence.ResolveAsync(second.Id, Whiskers.Models.Agent.ApprovalStatus.Approved,
            DateTime.UtcNow, "bob", DateTime.UtcNow.AddMinutes(10), stillDecidable: () => false));
        await using var check = Pg();
        Assert.Equal("Pending", (await check.Approvals.AsNoTracking().SingleAsync(a => a.Id == second.Id)).Status);
    }

    [SkippableFact]
    public async Task Audit_chain_writes_verifies_prunes_and_exports_on_real_postgres_with_retries_on()
    {
        Skip.IfNot(_up, "Docker daemon not available");
        // R1-23 with the production shape: retries on, so every chain transaction runs inside the execution strategy;
        // verification and export read a RepeatableRead snapshot.
        var dir = Path.Combine(Path.GetTempPath(), $"pg-chain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<MetricsDbContext>(
            services, o => o.UseNpgsql(_pg.GetConnectionString(), npg =>
            {
                npg.EnableRetryOnFailure(3);
                npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
            }));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Whiskers.Services.AuditLog.IAuditChain>(
            services, sp => new Whiskers.Services.AuditLog.AuditChain("pg-test-vault-key", Path.Combine(dir, "audit-checkpoint.json"),
                scopes: Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp)));
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        try
        {
            await using (var db = Pg()) await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
            var scopes = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp);
            var chain = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Whiskers.Services.AuditLog.IAuditChain>(sp);
            var audit = new Whiskers.Services.AuditLog.AuditLogService(scopes,
                NullLogger<Whiskers.Services.AuditLog.AuditLogService>.Instance);

            // Rows from before the chain: sealed at the first start, in pages.
            await using (var db = Pg())
            {
                for (var i = 0; i < 3; i++)
                    db.AuditLog.Add(new AuditLogEntity { Actor = "legacy", ActorType = "web", Action = "login", TargetType = "system",
                        Timestamp = DateTime.UtcNow.AddDays(-500) });
                await db.SaveChangesAsync();
            }
            await chain.SealExistingAsync();
            Assert.True(await audit.TryLogAtAsync(DateTime.UtcNow.AddDays(-400), "a@example.com", "web", "container.restart",
                "container", "c1", "old", details: "Ärger <&>"));
            Assert.True(await audit.TryLogAsync("a@example.com", "web", "container.restart", "container", "c2", "young"));
            await chain.CheckpointAsync();

            var intact = await chain.VerifyAsync();
            Assert.True(intact.Intact, string.Join("; ", intact.Problems));
            Assert.Equal(5, intact.LinksChecked);
            Assert.Equal(5, intact.TrustedCheckpointSeq);

            Assert.Equal(4, await chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));   // three legacy rows + the old one
            using var output = new MemoryStream();
            var exported = await chain.ExportAsync(output);
            Assert.True(exported.Intact, string.Join("; ", exported.Problems));
            Assert.Equal(2, exported.RowsChecked);   // the young row + the retention record

            // An outcome over an untouched row: checked and locked (FOR UPDATE) first, then its next version.
            Task<int> Complete(MetricsDbContext db, long id) => chain.UpdateAsync(db, [id], async token =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Phase, "outcome"), token);
                return (changed, (IReadOnlyCollection<long>)[id]);
            });
            long young;
            await using (var db = Pg())
            {
                young = await db.AuditLog.Where(a => a.TargetId == "c2").Select(a => a.Id).SingleAsync();
                Assert.Equal(1, await Complete(db, young));
            }

            // A change made directly in the database is found, and never given a next version.
            await using (var db = Pg())
                await db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Details\" = 'rewritten' WHERE \"TargetId\" = 'c2'");
            Assert.Contains((await chain.VerifyAsync()).Problems, p => p.Contains("was changed after it was written"));
            await using (var db = Pg())
                await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(db, young));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

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
            TestSqlite.ClearPool(srcPath);
            foreach (var f in new[] { srcPath, srcPath + "-wal", srcPath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
        }
    }

    [SkippableFact]
    public async Task The_cli_migration_keeps_the_sqlite_audit_checkpoint_aside()
    {
        Skip.IfNot(_up, "Docker daemon not available");
        // R1-23: PostgreSQL starts its own chain; a leftover checkpoint file would block it as a tampered trail.
        var root = Path.Combine(Path.GetTempPath(), $"sw-smoke-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new Whiskers.Configuration.DataPathOptions(root);
        try
        {
            await using (var src = Sqlite(paths.DbPath))
            {
                await src.Database.EnsureCreatedAsync();
                src.AuditLog.Add(new AuditLogEntity { Actor = "admin", ActorType = "web", Action = "login", TargetType = "system" });
                await src.SaveChangesAsync();
            }
            await File.WriteAllTextAsync(paths.AuditCheckpointJson, "{}");

            var rc = await SqliteToPostgresMigrator.RunAsync(paths, _pg.GetConnectionString(), TextWriter.Null);

            Assert.Equal(0, rc);
            Assert.False(File.Exists(paths.AuditCheckpointJson));
            Assert.Single(Directory.GetFiles(root, "audit-checkpoint.json.sqlite-*"));
        }
        finally
        {
            TestSqlite.ClearPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    private static MetricsDbContext Sqlite(string path) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={path}").Options);
}
