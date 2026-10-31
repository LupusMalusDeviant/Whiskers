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
    public async Task A_file_that_cannot_be_rewritten_keeps_its_plaintext_and_does_not_stop_the_start()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var serversBefore = File.ReadAllBytes(_paths.ServersJson);
        var settingsBefore = File.ReadAllBytes(_paths.AppSettingsJson);
        var vault = await Vault();
        var servers = await Servers();
        // Neither file can be replaced: the temporary file each write goes through is a directory.
        Directory.CreateDirectory(_paths.ServersJson + ".tmp");
        Directory.CreateDirectory(_paths.AppSettingsJson + ".tmp");

        await Migrate(vault, servers);   // no exception: the start goes on

        Assert.Equal(serversBefore, File.ReadAllBytes(_paths.ServersJson));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.AppSettingsJson));
        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(servers.GetServer("hetz")!, vault));

        // Fixed by the next start: the vault already holds both credentials under the same keys.
        Directory.Delete(_paths.ServersJson + ".tmp");
        Directory.Delete(_paths.AppSettingsJson + ".tmp");
        var restartedVault = await Vault();
        var restarted = await Servers();
        await Migrate(restartedVault, restarted);
        Assert.Equal(_cloudKey, CloudCredentials.KeyOf(restarted.GetServer("hetz")!, restartedVault));
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

    [Fact]
    public async Task Adoption_never_replaces_an_existing_vault_entry()
    {
        var vault = await Vault();
        var key = CredentialRefs.MigratedCloudKey("hetz");
        Assert.True(await vault.AdoptLegacyCredentialAsync(key, _cloudKey));
        Assert.False(await vault.AdoptLegacyCredentialAsync(key, "another-value-entirely"));
        Assert.Equal(_cloudKey, (await Vault()).GetSecret(key));
    }

    [Fact]
    public async Task Store_adoption_takes_only_the_fixed_migration_key_and_exactly_the_stored_plaintext()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var servers = await Servers();
        var settings = AppSettings();

        await Assert.ThrowsAsync<ArgumentException>(() => servers.AdoptCloudKeyRefAsync("hetz", _cloudKey, "kubeconfig:other"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.AdoptDnsTokenRefAsync(_dnsToken, "registry:cred"));
        Assert.False(await servers.AdoptCloudKeyRefAsync("hetz", "not-the-stored-key", CredentialRefs.MigratedCloudKey("hetz")));
        Assert.False(await settings.AdoptDnsTokenRefAsync("not-the-stored-token", CredentialRefs.MigratedDnsToken));

        Assert.True(await servers.AdoptCloudKeyRefAsync("hetz", _cloudKey, CredentialRefs.MigratedCloudKey("hetz")));
        Assert.True(await settings.AdoptDnsTokenRefAsync(_dnsToken, CredentialRefs.MigratedDnsToken));
        // Already adopted: a second call changes nothing.
        Assert.False(await servers.AdoptCloudKeyRefAsync("hetz", _cloudKey, CredentialRefs.MigratedCloudKey("hetz")));
        Assert.False(await settings.AdoptDnsTokenRefAsync(_dnsToken, CredentialRefs.MigratedDnsToken));
        AssertNoPlaintext(_cloudKey, _dnsToken);
    }

    [Fact]
    public async Task Settings_store_refuses_a_flattened_or_malformed_dns_section()
    {
        var settings = AppSettings();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["Dns:ApiToken"] = JsonValue.Create(_dnsToken)!,
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveSectionsAsync(new Dictionary<string, object>
        {
            ["Dns"] = JsonValue.Create(_dnsToken)!,
        }));
        AssertNoPlaintext(_dnsToken);
    }

    [Fact]
    public void A_dns_save_keeps_a_hidden_environment_token_hidden_and_never_writes_plaintext()
    {
        var hidden = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = "", ["DefaultTtl"] = 300 };
        var next = Whiskers.Services.Dns.DnsSettingsSection.Next(hidden, "infomaniak", null, ["example.org"], 600);
        Assert.Equal("", next["ApiToken"]!.GetValue<string>());
        Assert.Null(next["ApiTokenRef"]);

        var legacy = new JsonObject { ["ApiToken"] = _dnsToken };
        Assert.Equal(_dnsToken, Whiskers.Services.Dns.DnsSettingsSection.PlaintextToken(legacy));
        var moved = Whiskers.Services.Dns.DnsSettingsSection.Next(legacy, "infomaniak", "dns:apitoken:new", [], 300);
        Assert.Null(moved["ApiToken"]);
        Assert.Equal("dns:apitoken:new", moved["ApiTokenRef"]!.GetValue<string>());

        var named = new JsonObject { ["ApiTokenRef"] = "dns:apitoken:old" };
        Assert.Equal("dns:apitoken:old", Whiskers.Services.Dns.DnsSettingsSection.Next(named, "infomaniak", null, [], 300)["ApiTokenRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task Removing_a_server_removes_its_cloud_key_unless_another_server_names_it()
    {
        var vault = await Vault();
        var servers = await Servers();
        var shared = CredentialRefs.NewCloudKey("a");
        var own = CredentialRefs.NewCloudKey("c");
        await vault.SetSecretAsync(shared, _cloudKey);
        await vault.SetSecretAsync(own, _cloudKey + "-c");
        await servers.AddServerAsync(new ServerConfig { Id = "a", Name = "a", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = shared, IsDefault = true });
        await servers.AddServerAsync(new ServerConfig { Id = "b", Name = "b", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = shared });
        await servers.AddServerAsync(new ServerConfig { Id = "c", Name = "c", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = own });

        Assert.True(await ServerRemoval.RemoveAsync(servers, vault, servers.GetServer("b")!));
        Assert.Equal(_cloudKey, vault.GetSecret(shared));   // still named by server a
        Assert.True(await ServerRemoval.RemoveAsync(servers, vault, servers.GetServer("c")!));
        Assert.Null(vault.GetSecret(own));
    }

    [Fact]
    public async Task Removing_a_kubernetes_server_with_a_cloud_key_removes_both_secrets()
    {
        var vault = await Vault();
        var servers = await Servers();
        await servers.AddServerAsync(new ServerConfig { Id = "a", Name = "a", IsDefault = true });
        var cloudRef = CredentialRefs.NewCloudKey("k");
        var kubeRef = Whiskers.Services.Workloads.Kubernetes.KubernetesClientCache.KubeconfigVaultKey("k") + ":" + Guid.NewGuid().ToString("N");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        await vault.SetSecretAsync(kubeRef, "kubeconfig-" + Guid.NewGuid().ToString("N"));
        await servers.AddServerAsync(new ServerConfig
        {
            Id = "k", Name = "k", ConnectionType = ConnectionType.Kubernetes, KubeconfigRef = kubeRef,
            CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef,
        });

        Assert.True(await ServerRemoval.RemoveAsync(servers, vault, servers.GetServer("k")!));
        Assert.Null(vault.GetSecret(cloudRef));
        Assert.Null(vault.GetSecret(kubeRef));
    }

    [Fact]
    public async Task Removing_a_server_with_a_vault_key_and_no_usable_vault_reports_the_cleanup_as_failed()
    {
        var vault = await Vault(key: null);
        var servers = await Servers();
        await servers.AddServerAsync(new ServerConfig { Id = "a", Name = "a", IsDefault = true });
        await servers.AddServerAsync(new ServerConfig { Id = "b", Name = "b" });
        await servers.AddServerAsync(new ServerConfig { Id = "c", Name = "c", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = CredentialRefs.NewCloudKey("c") });

        Assert.False(await ServerRemoval.RemoveAsync(servers, vault, servers.GetServer("c")!));   // its key stays, unused
        Assert.Null(servers.GetServer("c"));
        Assert.True(await ServerRemoval.RemoveAsync(servers, vault, servers.GetServer("b")!));    // nothing in the vault
    }

    [Fact]
    public async Task Cloud_listing_marks_only_the_server_whose_key_the_locked_vault_holds()
    {
        var vault = await Vault();
        var servers = await Servers();
        var cloudRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        await servers.AddServerAsync(new ServerConfig { Id = "hetz", Name = "Hetzner box", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef });

        var disabled = await Vault(key: null);
        var cloud = new CloudControlService(servers, [], NullLogger<CloudControlService>.Instance, disabled);
        var listed = Assert.Single(await cloud.ListAllAsync());
        Assert.Equal("error", listed.Status);
        Assert.Equal("hetz", listed.WhiskersId);
        Assert.Contains("vault", listed.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_cloudKey, listed.Note);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.RebootAsync("hetz"));
        Assert.DoesNotContain(_cloudKey, ex.Message);
    }

    [Fact]
    public async Task Cloud_tools_answer_a_locked_vault_with_the_reason_not_an_exception()
    {
        var vault = await Vault();
        var servers = await Servers();
        var cloudRef = CredentialRefs.NewCloudKey("hetz");
        await vault.SetSecretAsync(cloudRef, _cloudKey);
        await servers.AddServerAsync(new ServerConfig { Id = "hetz", Name = "Hetzner box", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = cloudRef, IsDefault = true });
        // The real Hetzner provider; it is never reached, since the key cannot be read.
        var cloud = new CloudControlService(servers, [new Whiskers.Services.Cloud.Providers.HetznerCloudProvider(null!)],
            NullLogger<CloudControlService>.Instance, await Vault(key: null));
        // AUTH_DISABLED cookie caller (maps to admin) — only gets past the permission gate.
        var caller = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    Array.Empty<System.Security.Claims.Claim>(), Whiskers.Services.Auth.AuthConstants.AuthDisabledScheme)),
            },
        };

        var status = await Whiskers.Mcp.Tools.CloudTools.CloudStatus(caller, null!, cloud, "hetz");
        var rescue = await Whiskers.Mcp.Tools.HetznerTools.HetznerDisableRescue(caller, null!, cloud, null!, "hetz");

        foreach (var answer in new[] { status, rescue })
        {
            Assert.Contains("vault", answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_cloudKey, answer);
        }
    }

    [Fact]
    public async Task Migration_leaves_an_environment_dns_token_alone()
    {
        // The environment's token is not in app-settings.json: there is nothing for the migration to read or move, and
        // the configuration (environment, then app-settings.json on top, as the app layers them) still resolves to it.
        File.WriteAllText(_paths.AppSettingsJson, new JsonObject { ["Dns"] = new JsonObject { ["Provider"] = "infomaniak" } }.ToJsonString());
        var settingsBefore = File.ReadAllBytes(_paths.AppSettingsJson);
        var vault = await Vault();
        var envToken = "DNSTOKEN-" + Guid.NewGuid().ToString("N");

        await Migrate(vault, await Servers());

        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.AppSettingsJson));
        Assert.Empty(vault.ListSecrets());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Dns:ApiToken"] = envToken })   // the environment layer
            .AddJsonFile(_paths.AppSettingsJson, optional: false)
            .Build();
        var dns = configuration.GetSection(DnsSettings.SectionName).Get<DnsSettings>()!;
        Assert.Null(dns.ApiTokenRef);
        Assert.Equal(envToken, dns.ApiToken);
    }

    [Fact]
    public async Task A_legacy_to_vault_move_leaves_no_plaintext_in_the_recorded_audit_results()
    {
        WriteLegacyServers(_cloudKey);
        WriteLegacyDns(_dnsToken);
        var servers = await Servers();
        var results = new List<Whiskers.Services.Mutations.MutationDomainResult>();

        // What a page save records inside the gateway (R1-22 versions and setting diffs), before plaintext → after ref.
        using (var scope = Whiskers.Services.Mutations.MutationScope.Begin("server.save", "op-cloud"))
        {
            var record = servers.GetServer("hetz")!.Clone();
            await servers.UpdateServerAsync(record, null, CloudKeyChange.Set(CredentialRefs.NewCloudKey("hetz")));
            results.AddRange(scope.Results);
        }
        using (var scope = Whiskers.Services.Mutations.MutationScope.Begin("config.save", "op-dns"))
        {
            var newRef = CredentialRefs.NewDnsToken();
            await AppSettings().UpdateSectionAsync(DnsSettings.SectionName,
                stored => Whiskers.Services.Dns.DnsSettingsSection.Next(stored, "infomaniak", newRef, ["example.org"], 300));
            results.AddRange(scope.Results);
        }

        Assert.NotEmpty(results);
        var recorded = System.Text.Json.JsonSerializer.Serialize(results);
        Assert.DoesNotContain(_cloudKey, recorded);
        Assert.DoesNotContain(_dnsToken, recorded);
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
