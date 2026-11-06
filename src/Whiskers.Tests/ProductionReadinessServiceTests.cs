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

    [Fact]
    public async Task A_peer_whose_forwarded_headers_were_ignored_is_named_and_none_means_no_line()
    {
        // R1-50: a reverse proxy that still has to be listed in ForwardedHeaders:TrustedNetworks (or a spoofing client).
        var (vault, servers) = await Stores(null);
        var observer = new Whiskers.Utils.ForwardedHeadersObserver();
        async Task<ReadinessCheck?> Line() => (await new ProductionReadinessService(Config(null), vault,
                _sp.GetRequiredService<IServiceScopeFactory>(), servers, new AppSettingsStore(dataPaths: _paths), dataPaths: _paths,
                forwarded: observer).GetChecksAsync(isHttps: true))
            .SingleOrDefault(c => c.Title == "Reverse-Proxy vertraut");

        Assert.Null(await Line());

        observer.Untrusted(System.Net.IPAddress.Parse("172.18.0.2"));
        var line = await Line();

        Assert.NotNull(line);
        Assert.False(line!.Ok);
        Assert.Contains("172.18.0.2", line.Detail);
        Assert.Contains("ForwardedHeaders__TrustedNetworks__0", line.Detail);
    }

    [Fact]
    public async Task Disabled_authentication_warns_against_funnel_and_tcp_forwards()
    {
        // 2026-11-06 (DockTail evaluation): behind a TCP forward every visitor arrives from tailscaled's private address
        // and names its own Host, so the Auth:Disabled network check lets the internet in through Funnel.
        var (vault, servers) = await Stores(null);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Disabled"] = "true" }).Build();

        var line = (await new ProductionReadinessService(config, vault, _sp.GetRequiredService<IServiceScopeFactory>(),
                servers, new AppSettingsStore(dataPaths: _paths), dataPaths: _paths).GetChecksAsync(isHttps: true))
            .Single(c => c.Title == "Authentifizierung aktiv");

        Assert.False(line.Ok);
        Assert.Contains("TCP-Weiterleitung", line.Detail);
        Assert.Contains("Funnel", line.Detail);
        Assert.Contains("Internet", line.Detail);
    }

    [Theory]
    [InlineData(true, "false", true)]
    [InlineData(true, "true", false)]
    [InlineData(true, null, false)]
    [InlineData(false, "false", false)]
    public async Task Oidc_discovery_over_plain_http_is_named(bool enabled, string? requireHttps, bool named)
    {
        var (vault, servers) = await Stores(null);
        var values = new Dictionary<string, string?> { ["Oidc:Enabled"] = enabled ? "true" : "false" };
        if (requireHttps is not null) values["Oidc:RequireHttpsMetadata"] = requireHttps;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var checks = await new ProductionReadinessService(config, vault, _sp.GetRequiredService<IServiceScopeFactory>(),
            servers, new AppSettingsStore(dataPaths: _paths), dataPaths: _paths).GetChecksAsync(isHttps: true);

        Assert.Equal(named, checks.Any(c => c.Title == "OIDC-Discovery über HTTPS" && !c.Ok));
    }

    [Fact]
    public async Task Unreadable_store_copies_kept_in_the_data_directory_are_named_after_a_restart()
    {
        // R1-35: the copy outlasts the restart that follows a power loss; the in-memory recovery record does not.
        await File.WriteAllTextAsync(_paths.RolesJson + ".corrupt-20261101000000", "{torn");
        var (vault, servers) = await Stores(null);

        var line = (await Checks(null, vault, servers)).Single(c => c.Title == "Datenspeicher lesbar");

        Assert.False(line.Ok);
        Assert.Contains("roles.json.corrupt-20261101000000", line.Detail);
    }

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

    [Fact]
    public async Task The_vault_line_names_every_plaintext_key_kind_and_claims_no_more_than_file_protection()
    {
        // R1-51 (INV-56): since R1-44 there is one mTLS client key per host besides the shared one, all of them in
        // plaintext like the SSH keys; the line must not read as if one key were left or as if the vault held them.
        var (vault, servers) = await Stores("readiness-test-passphrase");

        var vaultCheck = (await Checks("readiness-test-passphrase", vault, servers)).Single(c => c.Title == VaultTitle);

        Assert.True(vaultCheck.Ok);
        Assert.Contains("ssh-keys/", vaultCheck.Detail);
        Assert.Contains("alle mTLS-Client-Schlüssel", vaultCheck.Detail);
        Assert.Contains("eigene jedes Hosts", vaultCheck.Detail);
        Assert.Contains("Dateirechte", vaultCheck.Detail);
        // Whiskers does not write the shared key and does not check the SSH keys' mode: no unconditional "0600".
        Assert.DoesNotContain("0600", vaultCheck.Detail);
        Assert.Contains("mTLS-Client-Schlüssel geschützt", vaultCheck.Detail);
        Assert.DoesNotContain("der mTLS-Client-Schlüssel im", vaultCheck.Detail);
        Assert.DoesNotContain("alle Secrets", vaultCheck.Detail);
    }

    // ---------------------------------------------------------------- contract 0006: the own container's exposure

    private async Task<ReadinessCheck?> SelfExposureLine(bool authDisabled, bool withDocker,
        Func<string, ContainerInfo[]>? containers = null, bool listThrows = false)
    {
        var (vault, servers) = await Stores(null);
        var self = Whiskers.Services.LogMonitor.LogMonitorService.ResolveSelfServerIds(servers).Single();
        var docker = withDocker ? new FakeDocker(containers?.Invoke(self) ?? []) { ListContainersThrows = listThrows } : null;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Disabled"] = authDisabled ? "true" : "false" }).Build();
        var checks = await new ProductionReadinessService(config, vault, _sp.GetRequiredService<IServiceScopeFactory>(),
            servers, new AppSettingsStore(dataPaths: _paths), dataPaths: _paths, docker: docker,
            selfContainerNames: new HashSet<string>(["whiskers"], StringComparer.OrdinalIgnoreCase)).GetChecksAsync(isHttps: true);
        return checks.SingleOrDefault(c => c.Title == ProductionReadinessService.SelfExposureTitle);
    }

    private static ContainerInfo Self(string serverId, string name, params (string Key, string Value)[] labels) => new()
    {
        Id = name + "-id", Name = name, ServerId = serverId,
        Labels = labels.ToDictionary(l => l.Key, l => l.Value),
    };

    [Fact]
    public async Task The_own_container_published_through_funnel_with_sign_in_off_is_not_ok_and_quotes_only_the_key()
    {
        var line = await SelfExposureLine(true, true, id =>
            [Self(id, "whiskers", ("docktail.funnel.enable", "true"), ("docktail.funnel.port", "secret-value-443"))]);

        Assert.NotNull(line);
        Assert.False(line!.Ok);
        Assert.Contains("Whiskers itself is published through `docktail.funnel.", line.Detail);
        Assert.Contains("with sign-in turned off", line.Detail);
        Assert.DoesNotContain("secret-value", line.Detail);
        Assert.DoesNotContain("true", line.Detail);
    }

    [Fact]
    public async Task Without_a_label_or_with_sign_in_on_there_is_no_line()
    {
        Assert.Null(await SelfExposureLine(true, true, id =>
            [Self(id, "whiskers", ("com.docker.compose.project", "whiskers"))]));
        Assert.Null(await SelfExposureLine(false, true, id =>
            [Self(id, "whiskers", ("docktail.funnel.enable", "true"))]));
    }

    [Fact]
    public async Task Another_container_with_a_route_label_is_not_the_own_one()
    {
        Assert.Null(await SelfExposureLine(true, true, id =>
            [Self(id, "whiskers"), Self(id, "blog", ("traefik.http.routers.blog.rule", "Host(`blog.example.org`)"))]));
    }

    [Fact]
    public async Task An_own_container_that_cannot_be_found_or_read_is_unknown_not_ok()
    {
        var missing = await SelfExposureLine(true, true, id => [Self(id, "something-else")]);
        var unreadable = await SelfExposureLine(true, true, listThrows: true);
        var noDocker = await SelfExposureLine(true, false);

        foreach (var line in new[] { missing, unreadable, noDocker })
        {
            Assert.NotNull(line);
            Assert.False(line!.Ok);
            Assert.StartsWith("Unknown", line.Detail);
        }
    }
}
