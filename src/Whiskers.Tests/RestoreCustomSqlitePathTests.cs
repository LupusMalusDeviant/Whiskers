using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>A restore on SQLite with its own connection string (WHISKERS_DB_CONNECTION) replaces the database the app
/// opens, not the default <c>metrics.db</c> (review 1.0.0, Data).</summary>
public sealed class RestoreCustomSqlitePathTests
{
    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task A_restore_replaces_the_sqlite_database_the_connection_string_names()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-review-restore-{Guid.NewGuid():N}");
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-review-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        var paths = new DataPathOptions(root);
        var custom = Path.Combine(elsewhere, "whiskers.db").Replace('\\', '/');
        // As the app registers it for WHISKERS_DB_PROVIDER=sqlite with WHISKERS_DB_CONNECTION set.
        var sp = new ServiceCollection()
            .AddWhiskersDbContexts("sqlite", $"Data Source={custom}", paths)
            .BuildServiceProvider();
        try
        {
            using (var scope = sp.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.Migrate();
                scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
            }
            async Task AddTaskAsync(string id)
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                db.ScheduledTasks.Add(new ScheduledTaskEntity { TaskId = id, Name = id, CronExpression = "0 3 * * *", Enabled = false });
                await db.SaveChangesAsync();
            }
            async Task<List<string>> TasksAsync()
            {
                using var scope = sp.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().ScheduledTasks.AsNoTracking()
                    .Select(t => t.TaskId).OrderBy(t => t).ToListAsync();
            }

            var svc = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
                new NoopLifetime(), new ConfigurationBuilder().Build(), NullLogger<BackupService>.Instance, paths);

            await AddTaskAsync("archived");
            var info = await svc.CreateBackupAsync("manual");
            await AddTaskAsync("added-after-the-backup");

            await svc.BeginRestoreAsync(svc.ResolveArchivePath(info.Id)!, "tester");
            Assert.True(File.Exists(paths.RestorePendingMarker));
            TestSqlite.ClearPools(root);
            TestSqlite.ClearPool(custom);
            RestoreBootHandler.ApplyPendingRestore(paths);
            Assert.False(File.Exists(paths.RestorePendingMarker));   // the restore says it was applied

            var after = await TasksAsync();
            Assert.True(after.SequenceEqual(["archived"]),
                $"The restore was applied, but the database the app uses ({custom}) still holds [{string.Join(", ", after)}]; " +
                $"the archive's database was written to {paths.DbPath} (exists: {File.Exists(paths.DbPath)}), which this " +
                "configuration never opens.");
        }
        finally
        {
            sp.Dispose();
            TestSqlite.ClearPools(root);
            TestSqlite.ClearPool(custom);
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void The_start_s_configuration_names_the_database_the_swap_replaces()
    {
        var paths = new DataPathOptions(Path.Combine(Path.GetTempPath(), $"sw-restore-target-{Guid.NewGuid():N}"));
        var custom = Path.Combine(Path.GetTempPath(), "elsewhere", "whiskers.db");
        var marker = new RestoreMarker { Provider = "sqlite", DatabasePath = Path.Combine(Path.GetTempPath(), "marker.db") };
        IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        // The configuration wins: it names what the app opens after this start.
        Assert.Equal(Path.GetFullPath(custom), RestoreBootHandler.SqliteTarget(paths, marker, Config(new()
        {
            ["WHISKERS_DB_PROVIDER"] = "sqlite", ["WHISKERS_DB_CONNECTION"] = $"Data Source={custom}",
        })));
        // Configured without a connection string: the default file, as the app registers it.
        Assert.Equal(Path.GetFullPath(paths.DbPath), RestoreBootHandler.SqliteTarget(paths, marker, Config(new()
        {
            ["WHISKERS_DB_PROVIDER"] = "sqlite",
        })));
        // No configuration: the file the app had open when the restore was committed; a marker of an earlier release
        // names none, so the default.
        Assert.Equal(Path.GetFullPath(marker.DatabasePath), RestoreBootHandler.SqliteTarget(paths, marker, null));
        Assert.Equal(Path.GetFullPath(paths.DbPath), RestoreBootHandler.SqliteTarget(paths, new RestoreMarker { Provider = "sqlite" }, null));
        // Old markers deserialize without the new field.
        var old = JsonSerializer.Deserialize<RestoreMarker>("""{"StagingDir":"/x","Provider":"sqlite"}""")!;
        Assert.Equal("", old.DatabasePath);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=shared;Mode=Memory")]
    [InlineData("")]
    public void A_connection_string_without_a_file_names_no_database_file(string connection)
        => Assert.Null(DatabaseRegistration.SqliteFilePath(connection));
}
