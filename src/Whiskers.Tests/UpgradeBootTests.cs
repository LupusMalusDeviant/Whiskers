using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Services.Backup;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-34 over the real start: an existing database one migration behind is backed up before it is migrated,
/// and when that backup cannot be taken the start fails with the database still unmigrated.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class UpgradeBootTests
{
    private static List<string> Applied(string dbPath)
    {
        using var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={dbPath}", o => o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)).Options);
        return db.Database.GetAppliedMigrations().ToList();
    }

    /// <summary>A data directory whose database is one metrics migration behind this release.</summary>
    private static (string DataDir, List<string> Before, string Latest) OneBehind()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var paths = new DataPathOptions(dataDir);
        using (var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>()
                   .UseSqlite($"Data Source={paths.DbPath}", o => o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)).Options))
        {
            var all = db.Database.GetMigrations().ToList();
            db.Database.Migrate(all[^2]);
            return (dataDir, Applied(paths.DbPath), all[^1]);
        }
    }

    private static async Task BootAsync(string dataDir, Func<WebApplicationFactory<Program>, Task> body)
    {
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        var config = new List<KeyValuePair<string, string?>>(BootSettings.AuthDisabled);
        config.AddRange(BootSettings.OnlyCore());
        try
        {
            using var bootSettings = BootSettings.Apply(config);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            await body(factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
        }
    }

    [Fact]
    public async Task A_start_on_an_older_database_backs_it_up_then_migrates_it()
    {
        var (dataDir, before, latest) = OneBehind();
        var paths = new DataPathOptions(dataDir);
        try
        {
            await BootAsync(dataDir, async factory =>
            {
                using var client = factory.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
                var backups = await factory.Services.GetRequiredService<IBackupService>().ListBackupsAsync();
                var backup = Assert.Single(backups);
                Assert.StartsWith(BackupService.PreUpgradeLabelPrefix, backup.Label);
            });
            Assert.Equal(latest, Applied(paths.DbPath)[^1]);
            Assert.NotEqual(before, Applied(paths.DbPath));
            Assert.False(File.Exists(paths.UpgradePendingMarker));        // the upgrade finished

            // The next start of the same release is no upgrade: no second backup.
            await BootAsync(dataDir, async factory =>
            {
                using var client = factory.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
                Assert.Single(await factory.Services.GetRequiredService<IBackupService>().ListBackupsAsync());
            });
        }
        finally
        {
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task A_start_whose_pre_upgrade_backup_fails_stops_with_the_database_unmigrated()
    {
        var (dataDir, before, _) = OneBehind();
        var paths = new DataPathOptions(dataDir);
        // A file where the backup directory belongs: the backup cannot be written.
        await File.WriteAllTextAsync(Path.Combine(dataDir, "backups"), "not a directory");
        try
        {
            await BootAsync(dataDir, factory =>
            {
                var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
                var all = string.Join(" | ", Flatten(ex).Select(e => e.Message));
                Assert.Contains("before any migration", all);
                return Task.CompletedTask;
            });
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, Applied(paths.DbPath));
        }
        finally
        {
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions.SelectMany(Flatten)) yield return inner;
        }
    }
}
