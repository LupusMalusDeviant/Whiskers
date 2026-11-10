using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part A, start migration (INV-52): legacy plaintext SSH keys and mTLS client keys (shared and per host)
// are sealed at start, each read back before it replaces the original.
public sealed class KeyMaterialMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "keymig-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;

    public KeyMaterialMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPathOptions(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<VaultService> Vault(string? passphrase)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(passphrase is null ? [] : new Dictionary<string, string?> { ["VAULT_KEY"] = passphrase })
            .Build();
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, dataPaths: _paths);
        await vault.InitializeAsync();
        return vault;
    }

    /// <summary>A legacy data directory: one SSH key, the shared client key and one per-host key, all plaintext.</summary>
    private Dictionary<string, string> WriteLegacyKeys()
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine(_paths.SshKeysDir, "edge", "id_ecdsa")] = SealedKeyFilesTests.NewPem(),
            [Path.Combine(_paths.MtlsDir, "client.key")] = SealedKeyFilesTests.NewPem(),
            [Path.Combine(_paths.MtlsDir, "hosts", "edge", "g1-s1", "client.key")] = SealedKeyFilesTests.NewPem(),
        };
        foreach (var (path, pem) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pem);
        }
        // known_hosts is not a key: it stays as it is.
        File.WriteAllText(Path.Combine(_paths.SshKeysDir, "known_hosts"), "host ecdsa-sha2-nistp256 AAAA\n");
        return files;
    }

    private KeyMaterialMigration Migration(IVaultService vault, Action<string>? afterSealing = null)
        => new(vault, new SealedKeyFiles(vault, _paths), _paths, NullLogger<KeyMaterialMigration>.Instance) { AfterSealingWritten = afterSealing };

    [Fact]
    public async Task Legacy_ssh_shared_and_per_host_keys_are_sealed_and_open_as_before()
    {
        var files = WriteLegacyKeys();
        var vault = await Vault("pass-one");

        var (sealedCount, plaintext) = Migration(vault).Run();

        Assert.Equal((3, 0), (sealedCount, plaintext));
        var keys = new SealedKeyFiles(vault, _paths);
        foreach (var (path, purpose) in KeyMaterialMigration.Candidates(_paths))
        {
            Assert.True(SealedKeyFiles.IsSealedFile(path), path);
            using var material = keys.Read(path, purpose);
            Assert.Equal(KeySource.Sealed, material.Source);
            Assert.Equal(files[path], material.Pem.ToString());
        }
        Assert.Equal((0, 0), KeyMaterialMigration.CountPlaintext(_paths));
        Assert.StartsWith("host ", File.ReadAllText(Path.Combine(_paths.SshKeysDir, "known_hosts")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.sealing", SearchOption.AllDirectories));
        Assert.Equal((0, 0), Migration(vault).Run());   // a second start has nothing to do
    }

    [Fact]
    public async Task An_interrupted_run_keeps_the_original_and_finishes_at_the_next_start()
    {
        var files = WriteLegacyKeys();
        var vault = await Vault("pass-one");
        var first = files.Keys.First();

        // A crash after the sealed copy was written, before it replaced the original.
        Assert.Throws<InvalidOperationException>(() => Migration(vault, p =>
        {
            if (p == first) throw new InvalidOperationException("crash");
        }).Run());
        Assert.Equal(files[first], File.ReadAllText(first));
        Assert.True(File.Exists(first + KeyMaterialMigration.SealingSuffix));
        // A leftover temporary file of an older build could hold a key in plaintext.
        File.WriteAllText(Path.Combine(_paths.MtlsDir, "client.key.tmp"), files[Path.Combine(_paths.MtlsDir, "client.key")]);

        var (sealedCount, plaintext) = Migration(vault).Run();

        Assert.Equal((3, 0), (sealedCount, plaintext));
        Assert.False(File.Exists(first + KeyMaterialMigration.SealingSuffix));
        Assert.False(File.Exists(Path.Combine(_paths.MtlsDir, "client.key.tmp")));
        using var opened = new SealedKeyFiles(vault, _paths).Read(first, KeyMaterialMigration.Candidates(_paths).Single(c => c.Path == first).Purpose);
        Assert.Equal(files[first], opened.Pem.ToString());
    }

    [Fact]
    public async Task Without_a_usable_vault_nothing_changes_and_the_files_are_counted()
    {
        var files = WriteLegacyKeys();
        var off = await Vault(null);
        Assert.Equal((0, 3), Migration(off).Run());
        Assert.Equal((1, 2), KeyMaterialMigration.CountPlaintext(_paths));
        foreach (var (path, pem) in files) Assert.Equal(pem, File.ReadAllText(path));

        await Vault("pass-one");   // the vault gets its key check
        var wrong = await Vault("another-pass");
        Assert.Equal(VaultKeyState.Wrong, wrong.KeyState);
        Assert.Equal((0, 3), Migration(wrong).Run());
        foreach (var (path, pem) in files) Assert.Equal(pem, File.ReadAllText(path));
    }
}
