using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// R1-28 (INV-54): the vault checks its key at start. A wrong key reads, writes and migrates nothing and leaves
// vault.json byte for byte as it was; legacy CBC entries are converted only after an untouched copy is kept.
public class VaultKeyCheckTests : IDisposable
{
    private const string RightKey = "right-passphrase-for-tests";
    private const string WrongKey = "wrong-passphrase-for-tests";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-tests", "vault-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_dir, "vault.json");
    private string CopyPath => VaultPath + VaultService.LegacyCopySuffix;

    public VaultKeyCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private async Task<VaultService> Open(string? key)
    {
        var config = key is null ? Config() : Config(("VAULT_KEY", key));
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, VaultPath);
        await vault.InitializeAsync();
        return vault;
    }

    private VaultData ReadFile() => JsonSerializer.Deserialize<VaultData>(File.ReadAllText(VaultPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private void WriteFile(VaultData data) => File.WriteAllText(VaultPath, JsonSerializer.Serialize(data));

    /// <summary>A pre-KDF entry: AES-256-CBC, key = SHA256(passphrase), IV prepended, base64. A fixed IV keeps the
    /// outcome of a wrong key deterministic.</summary>
    private static string LegacyCbc(string passphrase, string plain, byte ivSeed = 7)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        aes.IV = Enumerable.Repeat(ivSeed, 16).ToArray();
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), aes.IV);
        return Convert.ToBase64String(aes.IV.Concat(cipher).ToArray());
    }

    private static string Flip(string gcmValue)
    {
        var blob = Convert.FromBase64String(gcmValue["g1:".Length..]);
        blob[^1] ^= 0x01;
        return "g1:" + Convert.ToBase64String(blob);
    }

    // ---------------------------------------------------------------- right key, restart, salt

    [Fact]
    public async Task New_vault_writes_a_key_check_and_reads_its_secrets_after_a_restart()
    {
        var first = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, first.KeyState);
        await first.SetSecretAsync("db-password", "s3cret-value");

        var stored = ReadFile();
        Assert.NotNull(stored.KdfSalt);
        Assert.StartsWith("g1:", stored.KeyCheck);

        var second = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, second.KeyState);
        Assert.True(second.IsEnabled);
        Assert.Equal("s3cret-value", second.GetSecret("db-password"));

        var again = ReadFile();
        Assert.Equal(stored.KdfSalt, again.KdfSalt);   // the salt is persisted once, never regenerated
        Assert.Equal(stored.KeyCheck, again.KeyCheck);
    }

    [Fact]
    public async Task Vault_written_before_the_key_check_gets_one_once_an_entry_proves_the_key()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KeyCheck = null;
        WriteFile(data);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, reopened.KeyState);
        Assert.NotNull(ReadFile().KeyCheck);
        Assert.Equal("value-1", reopened.GetSecret("token"));
    }

    // ---------------------------------------------------------------- wrong key

    [Fact]
    public async Task Wrong_key_is_rejected_reads_nothing_writes_nothing_and_leaves_the_file_unchanged()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var before = File.ReadAllBytes(VaultPath);

        var wrong = await Open(WrongKey);
        Assert.Equal(VaultKeyState.Wrong, wrong.KeyState);
        Assert.False(wrong.IsEnabled);
        Assert.Null(wrong.GetSecret("token"));
        Assert.Empty(wrong.ListSecrets());
        var set = await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.SetSecretAsync("other", "x"));
        Assert.Contains("passt nicht", set.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.DeleteSecretAsync("token"));

        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.Equal("value-1", (await Open(RightKey)).GetSecret("token"));
    }

    [Fact]
    public async Task Wrong_key_on_an_older_vault_without_key_check_is_rejected_by_its_entries()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KeyCheck = null;
        WriteFile(data);
        var before = File.ReadAllBytes(VaultPath);

        var wrong = await Open(WrongKey);
        Assert.Equal(VaultKeyState.Wrong, wrong.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));   // no key check written for a key nothing proved
    }

    // ---------------------------------------------------------------- tampering

    [Fact]
    public async Task Tampered_key_check_locks_the_vault_even_for_the_right_key()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KeyCheck = Flip(data.KeyCheck!);
        WriteFile(data);
        var before = File.ReadAllBytes(VaultPath);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Wrong, reopened.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public async Task Tampered_entry_reads_as_null_while_the_key_check_still_proves_the_key()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("a", "value-a");
        await vault.SetSecretAsync("b", "value-b");
        var data = ReadFile();
        data.Entries.Single(e => e.Key == "a").EncryptedValue = Flip(data.Entries.Single(e => e.Key == "a").EncryptedValue);
        WriteFile(data);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, reopened.KeyState);
        Assert.Null(reopened.GetSecret("a"));
        Assert.Equal("value-b", reopened.GetSecret("b"));
    }

    [Fact]
    public async Task Vault_with_entries_but_no_salt_is_rejected_without_a_new_salt()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KdfSalt = null;
        WriteFile(data);
        var before = File.ReadAllBytes(VaultPath);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Wrong, reopened.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }

    // ---------------------------------------------------------------- disabled vault

    [Fact]
    public async Task Vault_without_a_key_is_not_configured_and_refuses_writes()
    {
        var vault = await Open(null);
        Assert.Equal(VaultKeyState.NotConfigured, vault.KeyState);
        Assert.False(vault.IsEnabled);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SetSecretAsync("k", "v"));
        Assert.Contains("nicht aktiviert", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.DeleteSecretAsync("k"));
        Assert.False(File.Exists(VaultPath));
    }

    // ---------------------------------------------------------------- VAULT_KEY_FILE

    [Fact]
    public async Task Vault_key_file_wins_over_the_plain_variable()
    {
        var file = Path.Combine(_dir, "vault_key");
        await File.WriteAllTextAsync(file, RightKey + "\n");
        var first = await Open(RightKey);
        await first.SetSecretAsync("token", "value-1");

        var config = Config(("VAULT_KEY_FILE", file), ("VAULT_KEY", WrongKey));
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, VaultPath);
        await vault.InitializeAsync();
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.Equal("value-1", vault.GetSecret("token"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_or_empty_vault_key_file_fails_closed(bool exists)
    {
        var file = Path.Combine(_dir, "vault_key");
        if (exists) File.WriteAllText(file, "  \n");
        var config = Config(("VAULT_KEY_FILE", file), ("VAULT_KEY", RightKey));

        var ex = Assert.Throws<SecretFileException>(() => new VaultService(config, NullLogger<VaultService>.Instance, VaultPath));
        Assert.Equal("VAULT_KEY_FILE", ex.Setting);
        Assert.DoesNotContain(RightKey, ex.Message);
        Assert.Throws<SecretFileException>(() => SecretSource.VaultKey(config));
    }

    [Fact]
    public void Secret_source_reads_the_plain_variable_without_a_file_setting()
    {
        Assert.Equal("plain", SecretSource.Read(Config(("SOME_SECRET", "plain")), "SOME_SECRET"));
        Assert.Null(SecretSource.Read(Config(("SOME_SECRET", "")), "SOME_SECRET"));
    }

    [Fact]
    public async Task Key_state_reaches_the_pages_through_the_mutation_scope_proxy()
    {
        // The app resolves IVaultService through MutationScopeProxy; KeyState is a default interface member.
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var wrong = await Open(WrongKey);

        var monitor = new Whiskers.Services.Mutations.MutationScopeMonitor(
            NullLogger<Whiskers.Services.Mutations.MutationScopeMonitor>.Instance);
        IVaultService guarded = Whiskers.Services.Mutations.MutationScopeProxy<IVaultService>.Wrap(wrong, monitor);
        Assert.NotSame(wrong, guarded);
        Assert.Equal(VaultKeyState.Wrong, guarded.KeyState);
        Assert.False(guarded.IsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_database_connection_file_stops_the_start(bool exists)
    {
        var file = Path.Combine(_dir, "db_connection");
        if (exists) File.WriteAllText(file, "");
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["WHISKERS_DB_CONNECTION_FILE"] = file;
        builder.Configuration["WHISKERS_DB_CONNECTION"] = "Data Source=" + Path.Combine(_dir, "x.db");

        var ex = Assert.Throws<SecretFileException>(() =>
            Whiskers.Services.Persistence.DatabaseRegistration.AddWhiskersDatabase(builder, new DataPathOptions(_dir)));
        Assert.Equal("WHISKERS_DB_CONNECTION_FILE", ex.Setting);
    }

    [Fact]
    public async Task Key_check_replaced_by_one_of_the_vaults_own_entries_locks_the_vault()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KeyCheck = data.Entries.Single().EncryptedValue;   // opens under the key, but is not the check text
        WriteFile(data);
        var before = File.ReadAllBytes(VaultPath);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Wrong, reopened.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }

    private sealed class NoLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task No_self_backup_is_sealed_with_a_key_the_vault_rejected()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var wrong = await Open(WrongKey);
        var paths = new DataPathOptions(_dir);
        using var sp = new ServiceCollection().BuildServiceProvider();

        var backup = new Whiskers.Services.Backup.BackupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Whiskers.Services.Maintenance.MaintenanceStateService(), new NoLifetime(),
            Config(("VAULT_KEY", WrongKey)), NullLogger<Whiskers.Services.Backup.BackupService>.Instance, paths, wrong);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => backup.CreateBackupAsync("manual"));
        Assert.Contains("does not match", ex.Message);
        Assert.False(Directory.Exists(paths.SelfBackupsDir) && Directory.EnumerateFiles(paths.SelfBackupsDir).Any());
    }

    // ---------------------------------------------------------------- legacy CBC

    [Fact]
    public async Task Legacy_entries_are_converted_after_an_untouched_recovery_copy_is_kept()
    {
        WriteFile(new VaultData
        {
            Entries =
            {
                new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a") },
                new VaultEntry { Key = "b", EncryptedValue = LegacyCbc(RightKey, "legacy value b, a little longer", 9) },
            },
        });
        var original = File.ReadAllBytes(VaultPath);

        var vault = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.Equal(original, File.ReadAllBytes(CopyPath));
        var stored = ReadFile();
        Assert.All(stored.Entries, e => Assert.StartsWith("g1:", e.EncryptedValue));
        Assert.NotNull(stored.KeyCheck);
        Assert.NotNull(stored.KdfSalt);

        var reopened = await Open(RightKey);
        Assert.Equal("legacy value a", reopened.GetSecret("a"));
        Assert.Equal("legacy value b, a little longer", reopened.GetSecret("b"));
    }

    [Fact]
    public async Task Differing_recovery_copy_is_never_overwritten_and_a_new_one_keeps_this_vault()
    {
        // A copy cut short by a crash, or the copy of another vault that a restore replaced.
        await File.WriteAllTextAsync(CopyPath, "{\"Entries\":[");
        WriteFile(new VaultData { Entries = { new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a") } } });
        var original = File.ReadAllBytes(VaultPath);

        var vault = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.Equal("{\"Entries\":[", await File.ReadAllTextAsync(CopyPath));
        var copies = Directory.GetFiles(_dir, "vault.json.legacy-cbc.*-*.bak");
        Assert.Single(copies);
        Assert.Equal(original, File.ReadAllBytes(copies[0]));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal("legacy value a", vault.GetSecret("a"));
    }

    [Fact]
    public async Task Identical_recovery_copy_from_an_earlier_attempt_is_used_as_it_is()
    {
        WriteFile(new VaultData { Entries = { new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a") } } });
        File.Copy(VaultPath, CopyPath);
        var original = File.ReadAllBytes(VaultPath);

        var vault = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.Equal(original, File.ReadAllBytes(CopyPath));
        Assert.Empty(Directory.GetFiles(_dir, "vault.json.legacy-cbc.*-*.bak"));
        Assert.StartsWith("g1:", ReadFile().Entries.Single().EncryptedValue);
    }

    [Fact]
    public async Task Legacy_entries_stay_unconverted_and_readable_when_no_copy_can_be_written()
    {
        WriteFile(new VaultData { Entries = { new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a") } } });
        Directory.CreateDirectory(CopyPath + ".tmp");   // the temporary copy cannot be created

        var vault = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.False(File.Exists(CopyPath));
        var stored = ReadFile();
        Assert.False(stored.Entries.Single().EncryptedValue.StartsWith("g1:", StringComparison.Ordinal));
        Assert.Null(stored.KeyCheck);   // no check while legacy entries remain
        Assert.Equal("legacy value a", vault.GetSecret("a"));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    public async Task Altered_salt_locks_the_vault(string salt)
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("token", "value-1");
        var data = ReadFile();
        data.KdfSalt = salt;
        WriteFile(data);
        var before = File.ReadAllBytes(VaultPath);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Wrong, reopened.KeyState);
        Assert.Empty(reopened.GetExpiringSecrets());
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public async Task Wrong_key_on_a_legacy_vault_migrates_nothing_and_writes_no_copy()
    {
        WriteFile(new VaultData
        {
            Entries =
            {
                new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a — long enough text") },
                new VaultEntry { Key = "b", EncryptedValue = LegacyCbc(RightKey, "legacy value b — long enough text", 9) },
            },
        });
        var before = File.ReadAllBytes(VaultPath);

        var wrong = await Open(WrongKey);
        Assert.Equal(VaultKeyState.Wrong, wrong.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task Legacy_entry_that_does_not_decrypt_locks_a_legacy_only_vault()
    {
        // One entry under another key: the others decrypt, but nothing proves this key is the vault's.
        WriteFile(new VaultData
        {
            Entries =
            {
                new VaultEntry { Key = "a", EncryptedValue = LegacyCbc(RightKey, "legacy value a") },
                new VaultEntry { Key = "b", EncryptedValue = LegacyCbc(WrongKey, "legacy value b — long enough text") },
            },
        });
        var before = File.ReadAllBytes(VaultPath);

        var vault = await Open(RightKey);
        Assert.Equal(VaultKeyState.Wrong, vault.KeyState);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task Legacy_entry_that_never_decrypts_in_a_proven_vault_gets_no_copy_on_any_start()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("new", "gcm value");
        var data = ReadFile();
        var broken = LegacyCbc(WrongKey, "legacy value — long enough text");
        data.Entries.Add(new VaultEntry { Key = "broken", EncryptedValue = broken });
        data.KeyCheck = null;   // written before R1-28: proven by its GCM entry only
        WriteFile(data);

        for (var i = 0; i < 2; i++)
        {
            var reopened = await Open(RightKey);
            Assert.Equal(VaultKeyState.Verified, reopened.KeyState);
            Assert.Equal("gcm value", reopened.GetSecret("new"));
        }
        Assert.Empty(Directory.GetFiles(_dir, "vault.json.legacy-cbc*"));
        var stored = ReadFile();
        Assert.Equal(broken, stored.Entries.Single(e => e.Key == "broken").EncryptedValue);
        Assert.NotNull(stored.KeyCheck);   // the key was proven: the check survives the loss of every GCM entry

        await (await Open(RightKey)).DeleteSecretAsync("new");
        Assert.Equal(VaultKeyState.Verified, (await Open(RightKey)).KeyState);
    }

    [Fact]
    public async Task Mixed_vault_converts_all_legacy_entries_once_the_key_check_proves_the_key()
    {
        var vault = await Open(RightKey);
        await vault.SetSecretAsync("new", "gcm value");
        var data = ReadFile();
        data.Entries.Add(new VaultEntry { Key = "old", EncryptedValue = LegacyCbc(RightKey, "legacy value") });
        WriteFile(data);
        var original = File.ReadAllBytes(VaultPath);

        var reopened = await Open(RightKey);
        Assert.Equal(VaultKeyState.Verified, reopened.KeyState);
        Assert.Equal(original, File.ReadAllBytes(CopyPath));
        Assert.All(ReadFile().Entries, e => Assert.StartsWith("g1:", e.EncryptedValue));
        Assert.Equal("legacy value", reopened.GetSecret("old"));
        Assert.Equal("gcm value", reopened.GetSecret("new"));
    }
}
