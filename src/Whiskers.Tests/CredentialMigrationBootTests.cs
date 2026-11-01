using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>R1-29, through the real host: a data directory with plaintext cloud and DNS credentials and a VAULT_KEY ends,
/// after a boot, with both files naming vault keys and the vault (the one the app resolves) holding the values — so the
/// migration's order after the vault and the server registry, and its wiring, are what the unit tests assume. Shares
/// the WebAppBoot collection so the process-wide WHISKERS_DATA_DIR env var can't leak into a parallel factory boot.</summary>
[Collection("WebAppBoot")]
public sealed class CredentialMigrationBootTests
{
    [Fact]
    public async Task A_boot_with_a_vault_moves_legacy_plaintext_credentials_into_it()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-r129-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var paths = new DataPathOptions(dataDir);
        // Built at runtime: never a token-shaped literal in the repository.
        var cloudKey = "CLOUDKEY-" + Guid.NewGuid().ToString("N");
        var dnsToken = "DNSTOKEN-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(paths.ServersJson, new JsonObject
        {
            ["Servers"] = new JsonArray(new JsonObject
            {
                ["Id"] = "hetz", ["Name"] = "Hetzner box", ["ConnectionType"] = 0, ["Enabled"] = true, ["IsDefault"] = true,
                ["CloudProvider"] = (int)CloudProvider.Hetzner, ["CloudApiKey"] = cloudKey,
            }),
        }.ToJsonString());
        var botToken = "BOTTOKEN-" + Guid.NewGuid().ToString("N");
        var agentKey = "AGENTKEY-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(paths.AppSettingsJson, new JsonObject
        {
            ["Dns"] = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = dnsToken },
            ["Telegram"] = new JsonObject { ["Enabled"] = true, ["BotToken"] = botToken, ["ChatId"] = "1" },
        }.ToJsonString());
        File.WriteAllText(paths.AgentSettingsJson, new JsonObject
        {
            ["Agent"] = new JsonObject { ["Provider"] = "openai", ["ApiKey"] = agentKey },
        }.ToJsonString());

        // R1-31: a data-protection key written in plaintext before (as the app did), and what it protected.
        string signedIn;
        using (var before = new ServiceCollection().AddDataProtection().SetApplicationName("ServerWatch")
                   .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir)).Services.BuildServiceProvider())
            signedIn = before.GetRequiredService<IDataProtectionProvider>().CreateProtector("boot-test").Protect("session");
        Assert.Contains("masterKey", string.Concat(Directory.GetFiles(paths.KeysDir, "*.xml").Select(File.ReadAllText)));

        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        var config = new List<KeyValuePair<string, string?>>();
        // All modules: the notification channels and the agent read their credentials from the vault after the boot.
        config.Add(new("Auth:Disabled", "true"));
        config.Add(new("VAULT_KEY", "r129-boot-test-passphrase-" + Guid.NewGuid().ToString("N")));

        try
        {
            using var bootSettings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var client = factory.CreateClient();   // starts the host: the initializers have run
            BootSettings.AssertBoot(factory.Services, config);

            var vault = factory.Services.GetRequiredService<IVaultService>();
            Assert.True(vault.IsEnabled);
            Assert.Equal(cloudKey, vault.GetSecret(CredentialRefs.MigratedCloudKey("hetz")));
            Assert.Equal(dnsToken, vault.GetSecret(CredentialRefs.MigratedDnsToken));

            var servers = File.ReadAllText(paths.ServersJson);
            var settings = File.ReadAllText(paths.AppSettingsJson);
            Assert.Contains(CredentialRefs.MigratedCloudKey("hetz"), servers);
            Assert.Contains(CredentialRefs.MigratedDnsToken, settings);
            Assert.DoesNotContain(cloudKey, servers);
            Assert.DoesNotContain(dnsToken, settings);

            // R1-30: moved too, and the options the app resolves read them from the vault (bound again after the start).
            Assert.DoesNotContain(botToken, settings);
            Assert.DoesNotContain(agentKey, File.ReadAllText(paths.AgentSettingsJson));
            Assert.Equal(botToken, factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TelegramSettings>>().CurrentValue.BotToken);
            Assert.Equal(agentKey, factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AgentSettings>>().CurrentValue.ApiKey);

            // R1-31: the key is encrypted in place at the start, and what it protected still opens — nobody signed out.
            Assert.DoesNotContain("masterKey", string.Concat(Directory.GetFiles(paths.KeysDir, "*.xml").Select(File.ReadAllText)));
            Assert.Equal("session", factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("boot-test").Unprotect(signedIn));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
