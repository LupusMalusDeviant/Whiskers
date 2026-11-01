using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>R1-31 (INV-52): the data-protection key ring (sign-in cookies, antiforgery) is written with each key's
/// secret encrypted by the vault, stays usable across a restart, is encrypted in place on upgrade without signing
/// anybody out, and with a VAULT_KEY that does not open the vault nothing is written in plaintext.</summary>
public sealed class DataProtectionKeyStorageTests : IDisposable
{
    private const string VaultKey = "dp-key-storage-test-passphrase";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "dp-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;

    public DataProtectionKeyStorageTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPathOptions(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static IConfiguration Config(string? vaultKey)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(vaultKey is null ? [] : new Dictionary<string, string?> { ["VAULT_KEY"] = vaultKey })
            .Build();

    private async Task<VaultService> Vault(string? key = VaultKey)
    {
        var vault = new VaultService(Config(key), NullLogger<VaultService>.Instance, dataPaths: _paths);
        await vault.InitializeAsync();
        return vault;
    }

    /// <summary>One process: Data Protection as the app registers it, with the vault started first.</summary>
    private ServiceProvider Process(IVaultService vault, string? vaultKey = VaultKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Config(vaultKey));
        services.AddSingleton(vault);
        services.AddDataProtection().SetApplicationName("ServerWatch");
        services.AddVaultProtectedKeyRing(_paths.KeysDir);
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider sp)
        => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");

    private string[] KeyFiles() => Directory.Exists(_paths.KeysDir) ? Directory.GetFiles(_paths.KeysDir, "*.xml") : [];

    private string KeyFilesText() => string.Concat(KeyFiles().Select(File.ReadAllText));

    [Fact]
    public async Task Keys_are_written_encrypted_and_the_ring_stays_usable_after_a_restart()
    {
        var vault = await Vault();
        string payload;
        using (var first = Process(vault))
            payload = Protector(first).Protect("session");

        Assert.NotEmpty(KeyFiles());
        var text = KeyFilesText();
        Assert.DoesNotContain("masterKey", text);
        Assert.Contains("vaultProtected", text);
        Assert.Equal(0, new VaultKeyRingRepository(_paths.KeysDir, vault, NullLogger<VaultKeyRingRepository>.Instance).CountPlaintextKeys());

        // A restart: a new vault instance on the same files, a new key ring read from the directory.
        var restarted = await Vault();
        using var second = Process(restarted);
        Assert.Equal("session", Protector(second).Unprotect(payload));
        Assert.Single(KeyFiles());
    }

    [Fact]
    public async Task Without_a_vault_key_the_keys_are_written_as_before_and_encrypted_in_place_once_one_is_set()
    {
        var off = await Vault(key: null);
        string payload;
        using (var first = Process(off, vaultKey: null))
            payload = Protector(first).Protect("session");
        Assert.Contains("masterKey", KeyFilesText());   // as before R1-31

        var vault = await Vault();
        var repository = new VaultKeyRingRepository(_paths.KeysDir, vault, NullLogger<VaultKeyRingRepository>.Instance);
        Assert.Equal(1, repository.CountPlaintextKeys());
        Assert.Equal(1, repository.ProtectExistingKeys());
        Assert.DoesNotContain("masterKey", KeyFilesText());
        Assert.Equal(0, repository.CountPlaintextKeys());
        Assert.Equal(0, repository.ProtectExistingKeys());

        // The same key, now encrypted: what it protected before still opens — nobody is signed out by the upgrade.
        using var second = Process(vault);
        Assert.Equal("session", Protector(second).Unprotect(payload));
    }

    [Fact]
    public async Task With_a_vault_key_that_does_not_open_the_vault_nothing_is_written_in_plaintext()
    {
        var vault = await Vault();
        string payload;
        using (var first = Process(vault))
            payload = Protector(first).Protect("session");
        var before = KeyFiles().ToDictionary(f => f, File.ReadAllText);

        var locked = await Vault("another-passphrase-entirely");
        Assert.Equal(VaultKeyState.Wrong, locked.KeyState);
        using var second = Process(locked, "another-passphrase-entirely");
        var protector = Protector(second);
        // Sign-in keeps working on a key kept in memory; what the vault's key protected cannot be opened.
        Assert.Equal("fresh", protector.Unprotect(protector.Protect("fresh")));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(payload));

        Assert.Equal(before, KeyFiles().ToDictionary(f => f, File.ReadAllText));
        Assert.DoesNotContain("masterKey", KeyFilesText());
    }

    [Fact]
    public async Task A_key_moved_into_another_purpose_is_not_opened()
    {
        var vault = await Vault();
        var stored = vault.ProtectField(ProtectedFields.DataProtectionKey, "<x/>")!;
        Assert.False(vault.TryUnprotectField(ProtectedFields.WebhookSecret("abc"), stored, out _));
        Assert.True(vault.TryUnprotectField(ProtectedFields.DataProtectionKey, stored, out var back));
        Assert.Equal("<x/>", back);
    }
}
