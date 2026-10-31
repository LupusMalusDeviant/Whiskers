using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Cloud;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// R1-29 (INV-52): cloud API keys and the DNS token are kept in the vault. After a save or the start-up migration no
// file of the data directory — servers.json, app-settings.json, the database, a self-backup — holds them in plaintext.
public class SecretsAtRestTests : IDisposable
{
    private const string VaultKey = "secrets-at-rest-test-passphrase";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "rest-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;

    // Built at runtime: never a token-shaped literal in the repository.
    private readonly string _cloudKey = "CLOUDKEY-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly string _dnsToken = "DNSTOKEN-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public SecretsAtRestTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPathOptions(_root);
    }

    public void Dispose()
    {
        TestSqlite.ClearPools(_root);
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

    private async Task<ServerConfigService> Servers()
    {
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://test" }),
            NullLogger<ServerConfigService>.Instance, dataPaths: _paths);
        await servers.InitializeAsync();
        return servers;
    }

    private AppSettingsStore AppSettings() => new(dataPaths: _paths);

    private async Task Migrate(IVaultService vault, IServerConfigService servers)
        => await new CredentialVaultMigration(vault, servers, AppSettings(), NullLogger<CredentialVaultMigration>.Instance)
            .InitializeAsync();

    /// <summary>Every file below the data directory — gzip archives unpacked — must not contain <paramref name="secrets"/>.</summary>
    private void AssertNoPlaintext(params string[] secrets)
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
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
            foreach (var secret in secrets)
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_root, file)} holds a credential in plaintext");
        }
    }

    private void WriteLegacyServers(string cloudKey)
        => File.WriteAllText(_paths.ServersJson, new JsonObject
        {
            ["Servers"] = new JsonArray(new JsonObject
            {
                ["Id"] = "hetz", ["Name"] = "Hetzner box", ["ConnectionType"] = 0, ["Enabled"] = true, ["IsDefault"] = true,
                ["CloudProvider"] = (int)CloudProvider.Hetzner, ["CloudApiKey"] = cloudKey,
            }),
        }.ToJsonString());

    private void WriteLegacyDns(string token)
        => File.WriteAllText(_paths.AppSettingsJson, new JsonObject
        {
            ["Dns"] = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = token, ["DefaultTtl"] = 300 },
        }.ToJsonString());

    // ---------------------------------------------------------------- saves

    [Fact]
    public async Task Cloud_and_dns_saves_keep_no_plaintext_in_any_file_or_backup()
    {
        var vault = await Vault();
        var servers = await Servers();
        var settings = AppSettings();

        // A cloud key as the Servers page saves it: into the vault under a new key, the record names that key.
        var cloudRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        await servers.AddServerAsync(new ServerConfig
        {
            Id = "hetz", Name = "Hetzner box", ConnectionType = ConnectionType.TCP,
            CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef,
        });
        // A rotation through an update.
        var rotatedKey = _cloudKey + "-rotated";
        var rotatedRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(rotatedRef, rotatedKey);
        await servers.UpdateServerAsync(servers.GetServer("hetz")!.Clone(), null, CloudKeyChange.Set(rotatedRef));

        // A DNS token as the Settings page saves it.
        var dnsRef = CredentialRefs.NewDnsToken();
        await vault.SetSecretAsync(dnsRef, _dnsToken);
        await settings.SaveSectionAsync(DnsSettings.SectionName, new JsonObject
        {
            ["Provider"] = "infomaniak", ["ApiTokenRef"] = dnsRef, ["DefaultTtl"] = 300,
        });

        Assert.Equal(rotatedKey, CloudCredentials.KeyOf(servers.GetServer("hetz")!, vault));
        await CreatePlaintextBackupAsync(vault);
        AssertNoPlaintext(_cloudKey, rotatedKey, _dnsToken);
    }

    [Fact]
    public async Task Stores_refuse_a_plaintext_credential()
    {
        var servers = await Servers();
        await Assert.ThrowsAsync<InvalidOperationException>(() => servers.AddServerAsync(new ServerConfig
        {
            Id = "plain", Name = "plain", CloudProvider = CloudProvider.Hetzner, CloudApiKey = _cloudKey,
        }));

        var settings = AppSettings();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveSectionAsync(DnsSettings.SectionName,
            new DnsSettings { ApiToken = _dnsToken }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["dns"] = new JsonObject { ["apitoken"] = _dnsToken },
        }));
        // An empty token (clearing it, or hiding one set by the environment) is no credential.
        await settings.SaveSectionAsync(DnsSettings.SectionName, new DnsSettings { ApiToken = "" });

        AssertNoPlaintext(_cloudKey, _dnsToken);
    }

    [Fact]
    public async Task An_update_from_an_old_copy_cannot_write_plaintext_or_point_back_to_an_old_key()
    {
        var vault = await Vault();
        var servers = await Servers();
        var cloudRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        await servers.AddServerAsync(new ServerConfig { Id = "hetz", Name = "h", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef });

        var stale = servers.GetServer("hetz")!.Clone();
        stale.CloudKeyRef = "cloudkey:hetz:old";
        stale.CloudApiKey = _cloudKey;   // e.g. a copy taken by onboarding or a page
        await servers.UpdateServerAsync(stale);

        var stored = servers.GetServer("hetz")!;
        Assert.Equal(cloudRef, stored.CloudKeyRef);
        Assert.Null(stored.CloudApiKey);
        AssertNoPlaintext(_cloudKey);
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public async Task Migration_moves_legacy_plaintext_into_the_vault_and_removes_it_from_every_file()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var vault = await Vault();
        var servers = await Servers();
        // The scan sees the plaintext while it is there (it proves the scan, not the migration).
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoPlaintext(_cloudKey));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoPlaintext(_dnsToken));

        await Migrate(vault, servers);

        var server = servers.GetServer("hetz")!;
        Assert.Equal(CredentialRefs.MigratedCloudKey("hetz"), server.CloudKeyRef);
        Assert.Null(server.CloudApiKey);
        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(server, vault));
        var dns = await AppSettings().ReadSectionAsync(DnsSettings.SectionName);
        Assert.Equal(CredentialRefs.MigratedDnsToken, dns!["ApiTokenRef"]!.GetValue<string>());
        Assert.Null(dns["ApiToken"]);
        Assert.Equal(_dnsToken, vault.GetSecret(CredentialRefs.MigratedDnsToken));

        // After a restart: the same state, read from disk.
        var reopenedVault = await Vault();
        var reopened = await Servers();
        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(reopened.GetServer("hetz")!, reopenedVault));
        await CreatePlaintextBackupAsync(reopenedVault);
        AssertNoPlaintext(_cloudKey, _dnsToken);
    }

    [Fact]
    public async Task Without_a_usable_vault_the_migration_keeps_the_plaintext_usable()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var serversBefore = File.ReadAllBytes(_paths.ServersJson);
        var settingsBefore = File.ReadAllBytes(_paths.AppSettingsJson);
        var vault = await Vault(key: null);
        var servers = await Servers();

        await Migrate(vault, servers);

        Assert.Equal(serversBefore, File.ReadAllBytes(_paths.ServersJson));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.AppSettingsJson));
        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(servers.GetServer("hetz")!, vault));
    }

    [Fact]
    public async Task Migration_removes_nothing_when_the_vault_cannot_confirm_the_write()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var serversBefore = File.ReadAllBytes(_paths.ServersJson);
        var settingsBefore = File.ReadAllBytes(_paths.AppSettingsJson);
        var servers = await Servers();

        await Migrate(new UnconfirmedVault(), servers);

        Assert.Equal(serversBefore, File.ReadAllBytes(_paths.ServersJson));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.AppSettingsJson));
        Assert.Equal(_cloudKey, servers.GetServer("hetz")!.CloudApiKey);
    }

    [Fact]
    public async Task Migration_interrupted_after_the_vault_write_finishes_at_the_next_start()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var vault = await Vault();
        // The vault already holds the credentials; the crash came before the files were rewritten.
        Assert.True(await vault.AdoptLegacyCredentialAsync(CredentialRefs.MigratedCloudKey("hetz"), _cloudKey));
        Assert.True(await vault.AdoptLegacyCredentialAsync(CredentialRefs.MigratedDnsToken, _dnsToken));

        var restartedVault = await Vault();
        var servers = await Servers();
        await Migrate(restartedVault, servers);

        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(servers.GetServer("hetz")!, restartedVault));
        Assert.Equal(_dnsToken, restartedVault.GetSecret(CredentialRefs.MigratedDnsToken));
        AssertNoPlaintext(_cloudKey, _dnsToken);
    }

    [Fact]
    public async Task Vault_adopts_only_migration_keys()
    {
        var vault = await Vault();
        await Assert.ThrowsAsync<ArgumentException>(() => vault.AdoptLegacyCredentialAsync("db-password", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.AdoptLegacyCredentialAsync("cloudkey:hetz:1234", "x"));
        var locked = await Vault(key: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            locked.AdoptLegacyCredentialAsync(CredentialRefs.MigratedDnsToken, _dnsToken));
    }

    [Fact]
    public async Task A_locked_vault_is_an_error_for_a_key_kept_in_it_never_a_silent_no_key()
    {
        var vault = await Vault();
        var cloudRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        var server = new ServerConfig { Id = "hetz", Name = "h", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef };

        var disabled = await Vault(key: null);
        var ex = Assert.Throws<InvalidOperationException>(() => CloudCredentials.KeyOf(server, disabled));
        Assert.DoesNotContain(_cloudKey, ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A vault that stores nothing it could read back (the write was lost).</summary>
    private sealed class UnconfirmedVault : IVaultService
    {
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => [];
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null, int? rotateAfterDays = null) => Task.CompletedTask;
        public string? GetSecret(string key) => null;
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public List<VaultEntry> GetExpiringSecrets() => [];
        public Task<bool> AdoptLegacyCredentialAsync(string migrationKey, string plaintext) => Task.FromResult(false);
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>A self-backup WITHOUT archive encryption, so its content can be scanned as it is.</summary>
    private async Task CreatePlaintextBackupAsync(IVaultService vault)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_paths.DbPath}"));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={_paths.DbPath}"));
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreatedAsync();

        var backup = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
            new NoLifetime(), Config(null), NullLogger<BackupService>.Instance, _paths, vault);
        var info = await backup.CreateBackupAsync("secrets-at-rest");
        Assert.False(info.Encrypted);
        TestSqlite.ClearPools(_root);
    }
}
