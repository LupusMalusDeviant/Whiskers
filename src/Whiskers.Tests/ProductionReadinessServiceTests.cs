using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Setup;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// R1-29 (INV-56): the readiness card reports where the cloud and DNS credentials actually are, not what the vault
// switch suggests, and never claims that every secret is encrypted.
public class ProductionReadinessServiceTests : IDisposable
{
    private const string CredentialsTitle = "Cloud- und DNS-Zugangsdaten im Vault";
    private const string VaultTitle = "Secret-Vault aktiv (VAULT_KEY)";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "ready-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;
    private readonly ServiceProvider _sp;
    private readonly string _cloudKey = "CLOUDKEY-" + Guid.NewGuid().ToString("N");

    public ProductionReadinessServiceTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPathOptions(_root);
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_paths.DbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPools(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static IConfiguration Config(string? vaultKey)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(vaultKey is null ? [] : new Dictionary<string, string?> { ["VAULT_KEY"] = vaultKey })
            .Build();

    private async Task<(VaultService Vault, ServerConfigService Servers)> Stores(string? vaultKey)
    {
        var vault = new VaultService(Config(vaultKey), NullLogger<VaultService>.Instance, dataPaths: _paths);
        await vault.InitializeAsync();
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://test" }),
            NullLogger<ServerConfigService>.Instance, dataPaths: _paths);
        await servers.InitializeAsync();
        return (vault, servers);
    }

    private async Task<IReadOnlyList<ReadinessCheck>> Checks(string? vaultKey, IVaultService vault, IServerConfigService servers)
        => await new ProductionReadinessService(Config(vaultKey), vault, _sp.GetRequiredService<IServiceScopeFactory>(),
            servers, new AppSettingsStore(dataPaths: _paths), dataPaths: _paths).GetChecksAsync(isHttps: true);

    private void WriteLegacyCredentials()
    {
        File.WriteAllText(_paths.ServersJson, new JsonObject
        {
            ["Servers"] = new JsonArray(new JsonObject
            {
                ["Id"] = "hetz", ["Name"] = "Hetzner box", ["ConnectionType"] = 0, ["Enabled"] = true, ["IsDefault"] = true,
                ["CloudProvider"] = (int)CloudProvider.Hetzner, ["CloudApiKey"] = _cloudKey,
            }),
        }.ToJsonString());
        File.WriteAllText(_paths.AppSettingsJson, new JsonObject
        {
            ["Dns"] = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = "DNSTOKEN-" + Guid.NewGuid().ToString("N") },
        }.ToJsonString());
    }

    [Fact]
    public async Task Plaintext_credentials_are_reported_even_with_the_vault_on()
    {
        WriteLegacyCredentials();
        var (vault, servers) = await Stores("readiness-test-passphrase");

        var checks = await Checks("readiness-test-passphrase", vault, servers);
        var credentials = checks.Single(c => c.Title == CredentialsTitle);
        Assert.False(credentials.Ok);
        Assert.Contains("1 Cloud-API-Key(s) in servers.json", credentials.Detail);
        Assert.Contains("DNS-Token in app-settings.json", credentials.Detail);
        Assert.Contains("noch nicht in den Vault übernommen", credentials.Detail);
        Assert.DoesNotContain(_cloudKey, credentials.Detail);

        // The vault line names what is still unencrypted instead of "secrets are stored encrypted".
        var vaultCheck = checks.Single(c => c.Title == VaultTitle);
        Assert.True(vaultCheck.Ok);
        Assert.Contains("Noch unverschlüsselt gespeichert", vaultCheck.Detail);
        Assert.DoesNotContain("Secrets werden verschlüsselt gespeichert", vaultCheck.Detail);
    }

    [Fact]
    public async Task Without_a_vault_the_plaintext_is_reported_with_how_it_gets_migrated()
    {
        WriteLegacyCredentials();
        var (vault, servers) = await Stores(null);

        var credentials = (await Checks(null, vault, servers)).Single(c => c.Title == CredentialsTitle);
        Assert.False(credentials.Ok);
        Assert.Contains("gültigem VAULT_KEY", credentials.Detail);
    }

    [Fact]
    public async Task After_the_migration_the_credentials_are_reported_as_in_the_vault()
    {
        WriteLegacyCredentials();
        var (vault, servers) = await Stores("readiness-test-passphrase");
        await new CredentialVaultMigration(vault, servers, new AppSettingsStore(dataPaths: _paths),
            NullLogger<CredentialVaultMigration>.Instance, dataPaths: _paths).InitializeAsync();

        var credentials = (await Checks("readiness-test-passphrase", vault, servers)).Single(c => c.Title == CredentialsTitle);
        Assert.True(credentials.Ok);
    }

    private const string SettingsTitle = "Benachrichtigungs- und KI-Zugangsdaten im Vault";

    [Fact]
    public async Task Plaintext_notification_and_ai_credentials_are_reported_by_name_until_migrated()
    {
        File.WriteAllText(_paths.AppSettingsJson, new JsonObject
        {
            ["Email"] = new JsonObject { ["Password"] = "SMTP-" + Guid.NewGuid().ToString("N") },
        }.ToJsonString());
        var agentKey = "AGENT-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(_paths.AgentSettingsJson, new JsonObject { ["Agent"] = new JsonObject { ["ApiKey"] = agentKey } }.ToJsonString());
        var (vault, servers) = await Stores("readiness-test-passphrase");

        var before = (await Checks("readiness-test-passphrase", vault, servers)).Single(c => c.Title == SettingsTitle);
        Assert.False(before.Ok);
        Assert.Contains("Email:Password", before.Detail);
        Assert.Contains("Agent:ApiKey", before.Detail);
        Assert.DoesNotContain(agentKey, before.Detail);

        await new CredentialVaultMigration(vault, servers, new AppSettingsStore(dataPaths: _paths),
            NullLogger<CredentialVaultMigration>.Instance, dataPaths: _paths).InitializeAsync();
        Assert.True((await Checks("readiness-test-passphrase", vault, servers)).Single(c => c.Title == SettingsTitle).Ok);

        // A plaintext value written by hand next to the reference is at rest all the same.
        var agent = JsonNode.Parse(File.ReadAllText(_paths.AgentSettingsJson))!.AsObject();
        agent["Agent"]!["ApiKey"] = "AGENT-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(_paths.AgentSettingsJson, agent.ToJsonString());
        var stray = (await Checks("readiness-test-passphrase", vault, servers)).Single(c => c.Title == SettingsTitle);
        Assert.False(stray.Ok);
        Assert.Contains("Agent:ApiKey", stray.Detail);
    }

    private const string StoredTitle = "Webhook-Secrets, Rollback-Snapshots und Sitzungsschlüssel verschlüsselt";

    [Fact]
    public async Task Plaintext_webhook_secrets_snapshots_and_key_files_are_counted_until_encrypted()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.Webhooks.Add(new WebhookEntity { Name = "legacy", Secret = "WH-" + Guid.NewGuid().ToString("N"), TargetId = "/x" });
            db.UpdateRollbacks.Add(new UpdateRollbackEntity { ContainerName = "web", ServerId = "local", ConfigJson = "{}" });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(_paths.KeysDir);
        File.WriteAllText(Path.Combine(_paths.KeysDir, "key-1.xml"),
            "<key><masterKey p4:requiresEncryption=\"true\" xmlns:p4=\"http://schemas.asp.net/2015/03/dataProtection\"><value>x</value></masterKey></key>");
        var (vault, servers) = await Stores("readiness-test-passphrase");
        var keyRing = new VaultKeyRingRepository(_paths.KeysDir, vault, NullLogger<VaultKeyRingRepository>.Instance);
        async Task<ReadinessCheck> Stored() => (await new ProductionReadinessService(Config("readiness-test-passphrase"), vault,
            _sp.GetRequiredService<IServiceScopeFactory>(), servers, keyRing: keyRing).GetChecksAsync(isHttps: true)).Single(c => c.Title == StoredTitle);

        var before = await Stored();
        Assert.False(before.Ok);
        Assert.Contains("1 Webhook-Secret(s) und 1 Rollback-Snapshot(s)", before.Detail);
        Assert.Contains("1 Sitzungsschlüssel-Datei(en)", before.Detail);

        using (var scope = _sp.CreateScope())
            await ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), vault, NullLogger.Instance);
        keyRing.ProtectExistingKeys();
        Assert.True((await Stored()).Ok);
    }

    [Fact]
    public async Task A_wrong_vault_key_is_reported_as_such()
    {
        var (first, _) = await Stores("readiness-test-passphrase");
        await first.SetSecretAsync("x", "y");
        var (vault, servers) = await Stores("another-passphrase-entirely");

        var vaultCheck = (await Checks("another-passphrase-entirely", vault, servers)).Single(c => c.Title == VaultTitle);
        Assert.False(vaultCheck.Ok);
        Assert.Contains("passt nicht", vaultCheck.Detail);
    }
}
