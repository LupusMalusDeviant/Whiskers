using System.Reflection;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Cloud;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-29, on the pages' own save paths: the cloud API key (Servers) and the DNS token (Settings) are written to
/// the vault under a new key that the record then names. Without a usable vault the save is refused, a save from a
/// stale page keeps the key named at the moment of writing, a rotation removes the old key, and a refused write
/// removes the new one.</summary>
public class CredentialPageSaveTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r129-page-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfigService _servers;
    private readonly AppSettingsStore _appSettings;
    private readonly SwitchableVault _vault = new();
    private readonly HookedRunner _runner = new();

    // Built at runtime: never a token-shaped literal in the repository.
    private static string Secret(string kind) => kind + "-" + Guid.NewGuid().ToString("N");

    public CredentialPageSaveTests()
    {
        Directory.CreateDirectory(_dir);
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"));
        _servers.InitializeAsync().GetAwaiter().GetResult();
        _appSettings = new AppSettingsStore(Path.Combine(_dir, "app-settings.json"));

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton<IVaultService>(_vault);
        Services.AddSingleton<ICurrentUserService>(new AdminUser());
        Services.AddSingleton<IUiMutationRunner>(_runner);
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.IDockerConnectionManager>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Onboarding.IOnboardingService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.AuditLog.IAuditLogService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Workloads.IWorkloadProviderFactory>.Create());
        Services.AddSingleton(NullProxy<IKubernetesClientCache>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Observability.ILoopSuspensionService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>.Create());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- Servers: cloud API key

    private IRenderedComponent<Servers> OpenServer(string serverId)
    {
        var page = Render<Servers>();
        Set(page, "_editingServer", _servers.GetServer(serverId)!.Clone());
        Set(page, "_form", _servers.GetServer(serverId)!.Clone());
        return page;
    }

    private async Task<ServerConfig> AddCloudServer(string apiKey)
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "cloud", ConnectionType = ConnectionType.Local, CloudProvider = CloudProvider.Hetzner };
        Set(page, "_form", server);
        Set(page, "_cloudApiKey", apiKey);
        await Persist(page);
        return _servers.GetServer(server.Id)!;
    }

    [Fact]
    public async Task Servers_refuses_a_cloud_key_without_a_usable_vault()
    {
        _vault.Enabled = false;
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "cloud", ConnectionType = ConnectionType.Local, CloudProvider = CloudProvider.Hetzner };
        Set(page, "_form", server);
        var apiKey = Secret("CLOUDKEY");
        Set(page, "_cloudApiKey", apiKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));

        Assert.Null(_servers.GetServer(server.Id));
        Assert.Empty(_vault.Keys);
        Assert.DoesNotContain(apiKey, AllFiles());
    }

    [Fact]
    public async Task Servers_rotation_removes_the_previous_cloud_key()
    {
        var server = await AddCloudServer(Secret("CLOUDKEY"));
        var page = OpenServer(server.Id);
        var rotated = Secret("CLOUDKEY");
        Set(page, "_cloudApiKey", rotated);
        await Persist(page);

        var key = Assert.Single(_vault.Keys);
        Assert.Equal(key, _servers.GetServer(server.Id)!.CloudKeyRef);
        Assert.Equal(rotated, CloudCredentials.KeyOf(_servers.GetServer(server.Id)!, _vault));
        Assert.DoesNotContain(rotated, AllFiles());
    }

    [Fact]
    public async Task Servers_save_from_a_stale_page_keeps_the_rotated_cloud_key()
    {
        var server = await AddCloudServer(Secret("CLOUDKEY"));
        var first = OpenServer(server.Id);
        var second = OpenServer(server.Id);   // opened before the rotation

        var rotated = Secret("CLOUDKEY");
        Set(first, "_cloudApiKey", rotated);
        await Persist(first);                  // rotates and removes the first key
        Get<ServerConfig>(second, "_form").Name = "renamed";
        await Persist(second);                 // a rename only, key field blank

        var live = _servers.GetServer(server.Id)!;
        Assert.Equal("renamed", live.Name);
        Assert.Equal(rotated, CloudCredentials.KeyOf(live, _vault));
        Assert.Single(_vault.Keys);
    }

    [Fact]
    public async Task Servers_clearing_the_provider_removes_the_cloud_key()
    {
        var server = await AddCloudServer(Secret("CLOUDKEY"));
        var page = OpenServer(server.Id);
        Get<ServerConfig>(page, "_form").CloudProvider = CloudProvider.None;
        await Persist(page);

        Assert.Null(_servers.GetServer(server.Id)!.CloudKeyRef);
        Assert.Empty(_vault.Keys);
    }

    [Fact]
    public async Task Servers_failed_record_write_removes_the_new_cloud_key()
    {
        var original = Secret("CLOUDKEY");
        var server = await AddCloudServer(original);
        var page = OpenServer(server.Id);
        Set(page, "_cloudApiKey", Secret("CLOUDKEY"));
        Directory.CreateDirectory(Path.Combine(_dir, "servers.json.tmp"));   // the record cannot be written

        await Assert.ThrowsAnyAsync<Exception>(() => Persist(page));

        Assert.Equal(original, CloudCredentials.KeyOf(_servers.GetServer(server.Id)!, _vault));
        Assert.Single(_vault.Keys);
    }

    // ---------------------------------------------------------------- Settings: DNS token

    /// <summary>The Settings page with only what the DNS panel uses: built, not rendered (the page needs every
    /// settings service to render).</summary>
    private Settings DnsPage(DnsSettings? options = null)
    {
        var page = new Settings();
        Inject(page, "VaultService", _vault);
        Inject(page, "AppSettings", _appSettings);
        Inject(page, "Mutations", _runner);
        Inject(page, "Snackbar", NullProxy<ISnackbar>.Create());
        Inject(page, "L", Services.GetRequiredService<IStringLocalizer<Whiskers.SharedResource>>());
        Inject(page, "DnsOptions", new FixedMonitor<DnsSettings>(options ?? new DnsSettings()));
        return page;
    }

    private static async Task SaveDns(Settings page, string? token = null, string zones = "example.org")
    {
        SetField(page, "_dnsProvider", "infomaniak");
        SetField(page, "_dnsDefaultTtl", 300);
        SetField(page, "_dnsAllowedZones", zones);
        SetField(page, "_dnsApiToken", token ?? "");
        await (Task)typeof(Settings).GetMethod("SaveDns", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
    }

    private async Task<string?> StoredDnsRef()
        => (await _appSettings.ReadSectionAsync(DnsSettings.SectionName))?["ApiTokenRef"]?.GetValue<string>();

    [Fact]
    public async Task Settings_refuses_a_dns_token_without_a_usable_vault()
    {
        _vault.Enabled = false;
        var token = Secret("DNSTOKEN");

        await SaveDns(DnsPage(), token);

        Assert.Null(await _appSettings.ReadSectionAsync(DnsSettings.SectionName));
        Assert.Empty(_vault.Keys);
        Assert.DoesNotContain(token, AllFiles());
    }

    [Fact]
    public async Task Settings_rotation_removes_the_previous_dns_token()
    {
        await SaveDns(DnsPage(), Secret("DNSTOKEN"));
        var rotated = Secret("DNSTOKEN");
        await SaveDns(DnsPage(), rotated);

        var key = Assert.Single(_vault.Keys);
        Assert.Equal(key, await StoredDnsRef());
        Assert.Equal(rotated, _vault.GetSecret(key));
        Assert.DoesNotContain(rotated, AllFiles());
    }

    [Fact]
    public async Task Settings_save_from_a_stale_page_keeps_the_dns_token_rotated_meanwhile()
    {
        await SaveDns(DnsPage(), Secret("DNSTOKEN"));
        var rotated = Secret("DNSTOKEN");

        // A zones-only save is under way; before its settings write lands, another admin rotates the token (and
        // removes the old key).
        _runner.BeforeOnce("config.save", () => SaveDns(DnsPage(), rotated));
        await SaveDns(DnsPage(), token: null, zones: "example.org, example.net");

        var stored = await _appSettings.ReadSectionAsync(DnsSettings.SectionName);
        Assert.Equal(2, stored!["AllowedZones"]!.AsArray().Count);
        Assert.Equal(rotated, _vault.GetSecret((await StoredDnsRef())!));
        Assert.Single(_vault.Keys);
    }

    [Fact]
    public async Task Settings_refused_settings_write_removes_the_new_dns_token_and_keeps_the_previous()
    {
        var original = Secret("DNSTOKEN");
        await SaveDns(DnsPage(), original);
        var before = await StoredDnsRef();

        _runner.RefuseOnce("config.save");
        await SaveDns(DnsPage(), Secret("DNSTOKEN"));

        Assert.Equal(before, await StoredDnsRef());
        Assert.Equal(original, _vault.GetSecret(before!));
        Assert.Single(_vault.Keys);
    }

    [Fact]
    public async Task Settings_save_moves_a_legacy_plaintext_token_into_the_vault()
    {
        var legacy = Secret("DNSTOKEN");
        File.WriteAllText(Path.Combine(_dir, "app-settings.json"),
            new JsonObject { ["Dns"] = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = legacy } }.ToJsonString());

        await SaveDns(DnsPage(new DnsSettings { ApiToken = legacy }));

        Assert.Equal(legacy, _vault.GetSecret((await StoredDnsRef())!));
        Assert.DoesNotContain(legacy, AllFiles());
    }

    // ---------------------------------------------------------------- helpers

    private string AllFiles() => string.Join("\n", Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));

    private static Task Persist(IRenderedComponent<Servers> page)
    {
        var method = typeof(Servers).GetMethod("PersistServerCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance, null)!);
    }

    private static void Set(IRenderedComponent<Servers> page, string field, object? value) => SetField(page.Instance, field, value);

    private static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static T Get<T>(IRenderedComponent<Servers> page, string field) =>
        (T)typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    private static void Inject(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }

    /// <summary>Runs every mutation, except that it can run something else first, or refuse, once per capability.</summary>
    private sealed class HookedRunner : IUiMutationRunner
    {
        private readonly Dictionary<string, Func<Task>> _before = new();
        private readonly HashSet<string> _refuse = new();

        public void BeforeOnce(string capabilityId, Func<Task> action) => _before[capabilityId] = action;
        public void RefuseOnce(string capabilityId) => _refuse.Add(capabilityId);

        private async Task GateAsync(string capabilityId)
        {
            if (_refuse.Remove(capabilityId))
                throw new MutationRefusedException("refused by the test", MutationStatus.Denied, "op");
            if (_before.Remove(capabilityId, out var before)) await before();
        }

        public async Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            await GateAsync(capabilityId);
            return await action(ct);
        }

        public async Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default)
        {
            await GateAsync(capabilityId);
            await action(ct);
        }

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class SwitchableVault : IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        public IEnumerable<string> Keys => _secrets.Keys;
        public bool Enabled { get; set; } = true;
        public bool IsEnabled => Enabled;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => _secrets.Keys.Select(k => new VaultEntry { Key = k }).ToList();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null)
        {
            if (!Enabled) throw new InvalidOperationException("vault disabled");
            _secrets[key] = plainValue;
            return Task.CompletedTask;
        }
        public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public Task DeleteSecretAsync(string key) { _secrets.TryRemove(key, out _); return Task.CompletedTask; }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }
}
