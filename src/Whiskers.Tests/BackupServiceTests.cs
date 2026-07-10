using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>End-to-end self-backup service against a real SQLite database: encrypted vs plaintext archives,
/// the snapshot exclude set (backups/ + *.tmp + live DB), list/validate round-trip, and the restore
/// compatibility checks (reject a newer schema / a provider mismatch; accept an equal-or-older schema).</summary>
public class BackupServiceTests
{
    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static (BackupService svc, DataPathOptions paths, string root, ServiceProvider sp) NewService(string? vaultKey)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new DataPathOptions(root);

        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}"));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}"));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(vaultKey is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["VAULT_KEY"] = vaultKey })
            .Build();

        var svc = new BackupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new MaintenanceStateService(),
            new NoopLifetime(),
            config,
            NullLogger<BackupService>.Instance,
            paths);
        return (svc, paths, root, sp);
    }

    private static void Cleanup(ServiceProvider sp, string root)
    {
        sp.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort (SQLite pool may hold the file) */ }
    }

    [Fact]
    public async Task Encrypted_backup_lists_and_validates_round_trip()
    {
        var (svc, _, root, sp) = NewService("test-vault-key");
        try
        {
            var info = await svc.CreateBackupAsync("manual");
            Assert.True(info.Encrypted);
            Assert.Equal("sqlite", info.Provider);

            var path = svc.ResolveArchivePath(info.Id)!;
            Assert.NotNull(path);
            var head = new byte[BackupArchiveCipher.MagicLength];
            await using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
            Assert.True(BackupArchiveCipher.StartsWithMagic(head));   // encrypted at rest

            var list = await svc.ListBackupsAsync();
            Assert.Single(list);
            Assert.Equal(info.Id, list[0].Id);

            var validation = await svc.ValidateArchiveAsync(path);
            Assert.True(validation.Ok);
            Assert.Equal("sqlite", validation.Manifest!.Provider);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Plaintext_backup_excludes_backups_dir_tmp_and_live_db()
    {
        var (svc, paths, root, sp) = NewService(vaultKey: null);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "roles.json"), "[]");
            Directory.CreateDirectory(Path.Combine(root, "keys"));
            await File.WriteAllTextAsync(Path.Combine(root, "keys", "ring.txt"), "K");
            Directory.CreateDirectory(paths.SelfBackupsDir);
            await File.WriteAllTextAsync(Path.Combine(paths.SelfBackupsDir, "old.tar.gz"), "OLD-BACKUP");
            await File.WriteAllTextAsync(Path.Combine(root, "scratch.tmp"), "TMP");

            var info = await svc.CreateBackupAsync("manual");
            Assert.False(info.Encrypted);

            var path = svc.ResolveArchivePath(info.Id)!;
            await using var fs = File.OpenRead(path);
            var inspection = await BackupArchiver.InspectAsync(fs, Path.Combine(root, "insp"));

            Assert.Contains("manifest.json", inspection.EntryNames);
            Assert.Contains("metrics.db", inspection.EntryNames);            // the VACUUM copy
            Assert.Contains("roles.json", inspection.EntryNames);
            Assert.Contains("keys/ring.txt", inspection.EntryNames);
            Assert.DoesNotContain(inspection.EntryNames, n => n.StartsWith("backups/", StringComparison.Ordinal));
            Assert.DoesNotContain(inspection.EntryNames, n => n.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_rejects_a_newer_schema()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root,
                new SelfBackupManifest { Provider = "sqlite", MetricsMigrations = new[] { "9999_from_the_future" } });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.False(result.Ok);
            Assert.Contains("newer", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_rejects_a_provider_mismatch()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "postgres" });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.False(result.Ok);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_accepts_an_equal_or_older_schema()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "sqlite" });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.True(result.Ok);
        }
        finally { Cleanup(sp, root); }
    }

    private static async Task<string> CraftManifestArchive(string root, SelfBackupManifest manifest)
    {
        var dir = Path.Combine(root, $"crafted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        var archivePath = Path.Combine(dir, "crafted.tar.gz");
        await using (var fs = File.Create(archivePath))
            await BackupArchiver.PackAsync(fs, new List<PackEntry> { new("manifest.json", manifestPath) });
        return archivePath;
    }
}
