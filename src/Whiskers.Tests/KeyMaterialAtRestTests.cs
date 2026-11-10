using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part A (INV-52): after an SSH upload, a per-host migration, a renewal and a rotation of a client
// credential, and after the start migration, no file of the data directory (.tmp, .sealing included) and no unencrypted
// self-backup holds a private-key header or any 24-character window of the probe keys' body.
public sealed class KeyMaterialAtRestTests
{
    // Built at runtime: never a key-shaped literal in the repository.
    private static readonly string[] Headers = ["PRIVATE " + "KEY-----", "OPENSSH " + "PRIVATE"];

    private static IEnumerable<string> Windows(string pem)
    {
        // The private scalar of a P-256 PKCS#8 key: bytes 36..67 of the DER, base64 characters 48..91. The rest of the body
        // is a fixed ASN.1 header and the public key, which the certificate rightly holds.
        var body = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal))).Trim();
        for (var i = 48; i + 24 <= 92; i += 4) yield return body.Substring(i, 24);
    }

    private static void AssertNoKeyMaterial(string root, IReadOnlyCollection<string> probePems)
    {
        var windows = probePems.SelectMany(Windows).ToList();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            if (file.EndsWith(".gz", StringComparison.Ordinal))
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var unpacked = new MemoryStream();
                gz.CopyTo(unpacked);
                bytes = unpacked.ToArray();
            }
            var text = Encoding.UTF8.GetString(bytes);
            var name = Path.GetRelativePath(root, file);
            foreach (var header in Headers) Assert.False(text.Contains(header, StringComparison.Ordinal), $"{name} holds a private-key header");
            foreach (var window in windows) Assert.False(text.Contains(window, StringComparison.Ordinal), $"{name} holds key material");
        }
    }

    [Fact]
    public async Task No_file_or_unencrypted_self_backup_holds_a_key_after_upload_rollout_renewal_rotation_and_start_migration()
    {
        using var w = new CredentialWorld();
        var probes = new List<string>();

        // The shared key (hand-made, plaintext) is sealed by the start migration.
        probes.Add(w.ReadKeyPem(Path.Combine(w.MtlsDir, "client.key")));
        new KeyMaterialMigration(TestVault.Usable, w.KeyFiles, w.Paths, NullLogger<KeyMaterialMigration>.Instance).Run();

        // Per-host migration, renewal and rotation: every key is written sealed.
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        probes.Add(w.ReadKeyPem(w.ConfigPaths("rabenhof").Key));
        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: false, default)).Ok);
        probes.Add(w.ReadKeyPem(w.ConfigPaths("rabenhof").Key));
        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: true, default)).Ok);
        probes.Add(w.ReadKeyPem(w.ConfigPaths("rabenhof").Key));

        // An SSH upload.
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, dataPaths: w.Paths, sealedKeys: w.KeyFiles);
        await servers.InitializeAsync();
        await servers.AddServerAsync(new ServerConfig { Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH, SshHost = "10.0.0.1" });
        var sshPem = SealedKeyFilesTests.NewPem();
        probes.Add(sshPem);
        await servers.SaveSshKeyAsync("edge", "id_ecdsa", Encoding.UTF8.GetBytes(sshPem));

        await CreatePlaintextBackupAsync(w.Paths, TestVault.Usable);

        AssertNoKeyMaterial(w.Root, probes);
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>A self-backup WITHOUT archive encryption, so its content can be scanned as it is.</summary>
    private static async Task CreatePlaintextBackupAsync(DataPathOptions paths, IVaultService vault)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}"));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}"));
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreatedAsync();

        var backup = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
            new NoLifetime(), new ConfigurationBuilder().Build(), NullLogger<BackupService>.Instance, paths, vault);
        var info = await backup.CreateBackupAsync("key-material-at-rest");
        Assert.False(info.Encrypted);
        TestSqlite.ClearPools(paths.RootDir);
    }
}
