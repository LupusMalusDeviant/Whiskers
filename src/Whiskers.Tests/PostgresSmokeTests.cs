using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>Smoke tests of the metrics database against a REAL PostgreSQL 18 (Testcontainers) — the automated
/// counterpart to the manual Docker-host proof (stableDB step 8). Marked <c>RequiresDocker</c>: skipped on a machine
/// without a Docker daemon, mandatory on CI (R1-47, <see cref="DockerRequirement"/>); run them with Docker up
/// (<c>dotnet test --filter Category=RequiresDocker</c>).</summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresSmokeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private bool _up;
    private Exception? _startFailure;

    public async Task InitializeAsync()
    {
        _startFailure = await DockerRequirement.TryStartAsync(_pg);
        _up = _startFailure is null;
    }

    private void SkipUnlessUp() => DockerRequirement.SkipUnlessStarted(_startFailure);

    public async Task DisposeAsync()
    {
        if (_up) await _pg.DisposeAsync();
    }

    private MetricsDbContext Pg() =>
        new(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(_pg.GetConnectionString(), o => o.MigrationsAssembly("Whiskers.Migrations.Postgres"))
            .Options);

    [SkippableFact]
    public async Task The_guardrail_policy_record_exists_after_migration_and_round_trips()
    {
        // Contract 0010: the InstanceMarkers table is created by the PostgreSQL migrations, and the record is inserted
        // once — a second ensure keeps the first row as it is.
        SkipUnlessUp();
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
        var marker = new Whiskers.Services.Guardrails.DbGuardrailPolicyMarker(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp));

        Assert.Null(await marker.RecordedAtAsync());
        Assert.False(await marker.PolicySnapshotsExistAsync());
        await marker.EnsureAsync("created");
        var first = await marker.RecordedAtAsync();
        await marker.EnsureAsync("loaded");

        Assert.NotNull(first);
        Assert.Equal(DateTimeKind.Utc, first.Value.Kind);
        Assert.Equal(first, await marker.RecordedAtAsync());
        await using var check = Pg();
        var row = await check.InstanceMarkers.AsNoTracking().SingleAsync();
        Assert.Equal("created", row.Detail);
    }

    [SkippableFact]
    public async Task Approval_decisions_and_uses_commit_on_real_postgres_with_retries_on()
    {
        SkipUnlessUp();
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

    /// <summary>INV-45 (review round 6, MINOR-3): a keyed refusal written twice — the webhook's "did not run" event
    /// from two paths — is one row on PostgreSQL too: the unique event id's violation rolls the second write back inside
    /// the chain's transaction (retries on), it reads the first row back, and the chain stays intact.</summary>
    [SkippableFact]
    public async Task A_keyed_refusal_written_twice_is_one_row_on_real_postgres_with_retries_on()
    {
        SkipUnlessUp();
        var dir = Path.Combine(Path.GetTempPath(), $"pg-keyed-{Guid.NewGuid():N}");
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
            var sink = new Whiskers.Services.Mutations.AuditLogMutationSink(scopes);
            var request = new Whiskers.Services.Mutations.MutationRequest("container.restart",
                new Whiskers.Services.Mutations.MutationPrincipal(Whiskers.Services.Mutations.MutationPrincipalKind.Automation,
                    "webhook:pg", "Webhook pg", McpPermissionLevels.Write),
                Whiskers.Services.Mutations.MutationSurface.Automation,
                new Whiskers.Services.Mutations.MutationTarget("container", "web-1"),
                new Dictionary<string, System.Text.Json.JsonElement>(), OperationId: "pg-run-1")
            {
                EventKey = Whiskers.Services.Webhooks.WebhookService.NotRunEventKey("pg-run-1"),
            };

            await sink.RecordDeniedAsync("pg-run-1", request, null, Whiskers.Services.Mutations.MutationStatus.Denied,
                "given up", CancellationToken.None);
            await sink.RecordDeniedAsync("pg-run-1", request, null, Whiskers.Services.Mutations.MutationStatus.Denied,
                "reconciled", CancellationToken.None);

            await using (var check = Pg())
            {
                var row = await check.AuditLog.AsNoTracking().SingleAsync(a => a.OperationId == "pg-run-1");
                Assert.Equal(Whiskers.Services.Webhooks.WebhookService.NotRunEventId("pg-run-1"), row.EventId);
            }
            var verified = await chain.VerifyAsync();
            Assert.True(verified.Intact, string.Join("; ", verified.Problems));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [SkippableFact]
    public async Task Audit_chain_writes_verifies_prunes_and_exports_on_real_postgres_with_retries_on()
    {
        SkipUnlessUp();
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
        SkipUnlessUp();

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
        SkipUnlessUp();

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
        SkipUnlessUp();
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

    [SkippableFact]
    public async Task A_failed_transfer_into_real_postgres_leaves_it_empty_and_runs_again()
    {
        SkipUnlessUp();
        // R1-34: metrics tables and sign-in accounts are copied in one PostgreSQL transaction.
        var root = Path.Combine(Path.GetTempPath(), $"sw-smoke-tx-{Guid.NewGuid():N}");
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
            await using (var id = SqliteIdentity(paths.DbPath))
            {
                await id.Database.MigrateAsync();
                id.Users.Add(new AppUser { Id = "user-1", UserName = "admin@example.org", NormalizedUserName = "ADMIN@EXAMPLE.ORG", PasswordHash = "HASH" });
                await id.SaveChangesAsync();
            }

            await using (var connection = new Npgsql.NpgsqlConnection(_pg.GetConnectionString()))
            await using (var target = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>()
                             .UseNpgsql(connection, o => o.MigrationsAssembly("Whiskers.Migrations.Postgres")).Options))
            await using (var idTarget = new WhiskersIdentityDbContext(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
                             .UseNpgsql(connection, o =>
                             {
                                 o.MigrationsAssembly("Whiskers.Migrations.Postgres");
                                 o.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
                             }).Options))
            await using (var src = Sqlite(paths.DbPath))
            await using (var idSrc = SqliteIdentity(paths.DbPath))
            {
                await target.Database.MigrateAsync();
                await idTarget.Database.MigrateAsync();
                await Assert.ThrowsAsync<IOException>(() => SqliteToPostgresMigrator.MigrateDataAsync(
                    src, target, TextWriter.Null, default, idSrc, idTarget,
                    (table, _) => table == "AspNetUsers" ? throw new IOException("disk gone") : Task.CompletedTask));
            }

            await using (var check = Pg())
                Assert.False(await check.AuditLog.AnyAsync());
            await using (var idCheck = PgIdentity())
                Assert.False(await idCheck.Users.AnyAsync());

            // Nothing was left behind, so the tool simply runs again.
            Assert.Equal(0, await SqliteToPostgresMigrator.RunAsync(paths, _pg.GetConnectionString(), TextWriter.Null));
            await using (var check = Pg())
                Assert.Equal(1, await check.AuditLog.CountAsync());
            await using (var idCheck = PgIdentity())
                Assert.Equal("user-1", (await idCheck.Users.SingleAsync()).Id);
        }
        finally
        {
            TestSqlite.ClearPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    private WhiskersIdentityDbContext PgIdentity() =>
        new(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseNpgsql(_pg.GetConnectionString(), o =>
            {
                o.MigrationsAssembly("Whiskers.Migrations.Postgres");
                o.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
            }).Options);

    private static WhiskersIdentityDbContext SqliteIdentity(string path) =>
        new(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseSqlite($"Data Source={path}", o =>
            {
                o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly);
                o.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
            }).Options);

    private static MetricsDbContext Sqlite(string path) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={path}").Options);
}
