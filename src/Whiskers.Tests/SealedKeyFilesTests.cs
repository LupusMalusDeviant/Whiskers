using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part A (INV-52): SSH and mTLS client keys are stored sealed by the vault, bound to their place.
public sealed class SealedKeyFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "sealed-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "sw-tests", "operator-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;

    public SealedKeyFilesTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _paths = new DataPathOptions(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { }
    }

    internal static string NewPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem() + "\n";
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

    private string KeyPath(string server = "edge", string name = "id_ed25519")
    {
        var dir = Path.Combine(_paths.SshKeysDir, server);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public async Task Round_trip_stores_no_plaintext_and_opens_with_its_purpose()
    {
        var files = new SealedKeyFiles(await Vault("pass-one"), _paths);
        var pem = NewPem();
        var path = KeyPath();
        files.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), pem);

        var stored = File.ReadAllText(path);
        Assert.StartsWith(ProtectedFields.Prefix, stored);
        Assert.DoesNotContain("PRIVATE KEY", stored);
        Assert.False(File.Exists(path + ".tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        using var material = files.Read(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"));
        Assert.Equal(KeySource.Sealed, material.Source);
        Assert.Equal(pem, material.Pem.ToString());
        Assert.True(SealedKeyFiles.IsSealedFile(path));
    }

    [Fact]
    public async Task A_file_moved_to_another_server_or_slot_is_refused()
    {
        var files = new SealedKeyFiles(await Vault("pass-one"), _paths);
        var path = KeyPath("edge");
        files.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem());
        var moved = KeyPath("core");
        File.Copy(path, moved);

        var refused = Assert.Throws<SealedKeyException>(() => files.Read(moved, SealedKeyPurposes.SshKey("core", "id_ed25519")));
        Assert.Contains("does not open here", refused.Message);

        // The same for an mTLS slot: the purpose names the slot.
        var slotA = Path.Combine(_paths.MtlsDir, "hosts", "a", "g1-s1", "client.key");
        var slotB = Path.Combine(_paths.MtlsDir, "hosts", "a", "g1-s2", "client.key");
        Directory.CreateDirectory(Path.GetDirectoryName(slotA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(slotB)!);
        files.Write(slotA, SealedKeyPurposes.MtlsClientKey(_paths, slotA), NewPem());
        File.Copy(slotA, slotB);
        Assert.Equal("mtls-client-key:hosts/a/g1-s2/client.key", SealedKeyPurposes.MtlsClientKey(_paths, slotB));
        Assert.Throws<SealedKeyException>(() => files.Read(slotB, SealedKeyPurposes.MtlsClientKey(_paths, slotB)));
    }

    [Fact]
    public async Task An_altered_file_is_refused()
    {
        var files = new SealedKeyFiles(await Vault("pass-one"), _paths);
        var path = KeyPath();
        files.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem());
        var text = File.ReadAllText(path).Trim();
        var bytes = Convert.FromBase64String(text[ProtectedFields.Prefix.Length..]);
        bytes[^1] ^= 0x01;
        File.WriteAllText(path, ProtectedFields.Prefix + Convert.ToBase64String(bytes));

        Assert.Throws<SealedKeyException>(() => files.Read(path, SealedKeyPurposes.SshKey("edge", "id_ed25519")));
    }

    [Fact]
    public async Task A_locked_or_missing_vault_refuses_with_a_named_reason()
    {
        var path = KeyPath();
        new SealedKeyFiles(await Vault("pass-one"), _paths).Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem());

        var wrong = await Vault("another-pass");
        Assert.Equal(VaultKeyState.Wrong, wrong.KeyState);
        var locked = Assert.Throws<SealedKeyException>(() => new SealedKeyFiles(wrong, _paths).Read(path, SealedKeyPurposes.SshKey("edge", "id_ed25519")));
        Assert.Equal(SealedKeyFiles.VaultLockedReason, locked.Message);

        var offVault = await Vault(null);
        var off = Assert.Throws<SealedKeyException>(() => new SealedKeyFiles(offVault, _paths).Read(path, SealedKeyPurposes.SshKey("edge", "id_ed25519")));
        Assert.Equal(SealedKeyFiles.VaultOffReason, off.Message);
    }

    [Fact]
    public async Task A_write_without_a_usable_vault_writes_nothing()
    {
        var path = KeyPath();
        var off = new SealedKeyFiles(await Vault(null), _paths);
        Assert.NotNull(off.RefusalReason);
        Assert.Throws<SealedKeyException>(() => off.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem()));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        await Vault("pass-one");   // gives vault.json its key check
        var wrong = new SealedKeyFiles(await Vault("another-pass"), _paths);
        Assert.NotNull(wrong.RefusalReason);
        Assert.Throws<SealedKeyException>(() => wrong.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Plaintext_inside_is_legacy_and_outside_is_operator_managed()
    {
        var files = new SealedKeyFiles(await Vault("pass-one"), _paths);
        var inside = KeyPath();
        var pem = NewPem();
        File.WriteAllText(inside, pem);
        using (var legacy = files.Read(inside, SealedKeyPurposes.SshKey("edge", "id_ed25519")))
        {
            Assert.Equal(KeySource.PlaintextInDataDirectory, legacy.Source);
            Assert.Equal(pem, legacy.Pem.ToString());
        }

        var outside = Path.Combine(_outside, "client.key");
        File.WriteAllText(outside, pem);
        using var managed = files.Read(outside, SealedKeyPurposes.MtlsClientKey(_paths, outside));
        Assert.Equal(KeySource.OperatorManaged, managed.Source);
        Assert.False(SealedKeyFiles.IsSealedFile(outside));
    }

    [Fact]
    public async Task The_buffer_is_zeroed_on_dispose()
    {
        var files = new SealedKeyFiles(await Vault("pass-one"), _paths);
        var path = KeyPath();
        files.Write(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"), NewPem());
        var material = files.Read(path, SealedKeyPurposes.SshKey("edge", "id_ed25519"));
        Assert.False(material.IsZeroed);
        material.Dispose();
        Assert.True(material.IsZeroed);
        Assert.Throws<ObjectDisposedException>(() => material.Pem.Length);
    }
}
