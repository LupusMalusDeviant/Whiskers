using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-11, on the Servers page's own save path: a kubeconfig change takes effect only with the saved server
/// record. The dialog buffer never becomes the live record, a failed retry leaves the working kubeconfig in place,
/// and a stale second dialog cannot point the server back at a key another save already removed.</summary>
public class ServersKubeconfigSaveTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r111-srv-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly ServerConfigService _servers;
    private readonly MemoryVault _vault = new();

    public ServersKubeconfigSaveTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "servers.json");
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: _path);
        _servers.InitializeAsync().GetAwaiter().GetResult();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(Null<IKeyInterceptorService>());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton<IVaultService>(_vault);
        Services.AddSingleton<ICurrentUserService>(new AdminUser());
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        Services.AddSingleton(Null<Whiskers.Services.Docker.IDockerConnectionManager>());
        Services.AddSingleton(Null<Whiskers.Services.Onboarding.IOnboardingService>());
        Services.AddSingleton(Null<Whiskers.Services.AuditLog.IAuditLogService>());
        Services.AddSingleton(Null<Whiskers.Services.Workloads.IWorkloadProviderFactory>());
        Services.AddSingleton(Null<IKubernetesClientCache>());
        Services.AddSingleton(Null<Whiskers.Services.Observability.ILoopSuspensionService>());
        Services.AddSingleton(Null<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_failed_retry_after_a_successful_save_keeps_the_working_kubeconfig()
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", "yaml-A");
        await Persist(page);   // saved; the probe then fails and the dialog stays open on the same buffer

        var live = _servers.GetServer(server.Id)!;
        Assert.NotSame(Get<ServerConfig>(page, "_form"), live);
        Assert.Equal("yaml-A", _vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(live)));

        Set(page, "_kubeconfigYaml", "yaml-B");
        Directory.CreateDirectory(_path + ".tmp");   // the retry cannot write servers.json
        await Assert.ThrowsAnyAsync<Exception>(() => Persist(page));

        live = _servers.GetServer(server.Id)!;
        Assert.Equal("yaml-A", _vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(live)));
        Assert.DoesNotContain(_vault.Keys, k => _vault.GetSecret(k) == "yaml-B");   // the staged key was removed
    }

    [Fact]
    public async Task A_stale_dialog_keeps_the_kubeconfig_another_save_rotated_to()
    {
        var setup = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(setup, "_form", server);
        Set(setup, "_kubeconfigYaml", "yaml-A");
        await Persist(setup);

        // Two admins open the same server.
        var first = Render<Servers>();
        var second = Render<Servers>();
        foreach (var page in new[] { first, second })
        {
            Set(page, "_editingServer", _servers.GetServer(server.Id)!.Clone());
            Set(page, "_form", _servers.GetServer(server.Id)!.Clone());
        }

        Set(first, "_kubeconfigYaml", "yaml-B");
        await Persist(first);                         // rotates A → B and removes A
        Get<ServerConfig>(second, "_form").Name = "renamed";
        await Persist(second);                        // a rename only, from the dialog opened before

        var live = _servers.GetServer(server.Id)!;
        Assert.Equal("renamed", live.Name);
        Assert.Equal("yaml-B", _vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(live)));
    }

    [Fact]
    public async Task A_save_queued_behind_a_rotation_keeps_the_rotated_kubeconfig()
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", "yaml-A");
        await Persist(page);
        var stale = _servers.GetServer(server.Id)!.Clone();   // what a second admin read before the rotation

        Set(page, "_kubeconfigYaml", "yaml-B");
        await Persist(page);                                   // rotation A → B, A removed

        // The second admin's write reaches the store now, carrying the reference it read before.
        stale.Name = "renamed";
        await _servers.UpdateServerAsync(stale);   // any update — onboarding and SSH-key cleanup use this one too

        var live = _servers.GetServer(server.Id)!;
        Assert.Equal("renamed", live.Name);
        Assert.Equal("yaml-B", _vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(live)));
    }

    [Fact]
    public async Task Deleting_from_a_stale_page_removes_the_current_kubeconfig()
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", "yaml-A");
        await Persist(page);
        var shown = _servers.GetServer(server.Id)!.Clone();   // the row a second page still shows

        Set(page, "_kubeconfigYaml", "yaml-B");
        await Persist(page);

        Assert.True(await ServerRemoval.RemoveAsync(_servers, _vault, shown));
        Assert.Empty(_vault.Keys);
    }

    [Fact]
    public async Task A_rotation_removes_the_previous_key()
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", "yaml-A");
        await Persist(page);
        Set(page, "_kubeconfigYaml", "yaml-B");
        await Persist(page);

        var key = Assert.Single(_vault.Keys);
        Assert.Equal("yaml-B", _vault.GetSecret(key));
        Assert.Equal(key, KubernetesClientCache.KubeconfigKeyOf(_servers.GetServer(server.Id)!));
    }

    private static Task Persist(IRenderedComponent<Servers> page)
    {
        var method = typeof(Servers).GetMethod("PersistServerCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance, null)!);
    }

    private static void Set(IRenderedComponent<Servers> page, string field, object? value) =>
        typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page.Instance, value);

    private static T Get<T>(IRenderedComponent<Servers> page, string field) =>
        (T)typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    /// <summary>A do-nothing implementation of <typeparamref name="T"/>: default values and completed tasks.</summary>
    private static T Null<T>() where T : class => NullProxy<T>.Create();

    public class NullProxy<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, NullProxy<T>>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }

    private sealed class PassThrough : IUiMutationRunner
    {
        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);
        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);
    }

    private sealed class MemoryVault : IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        public IEnumerable<string> Keys => _secrets.Keys;
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => _secrets.Keys.Select(k => new VaultEntry { Key = k }).ToList();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) { _secrets[key] = plainValue; return Task.CompletedTask; }
        public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public Task DeleteSecretAsync(string key) { _secrets.TryRemove(key, out _); return Task.CompletedTask; }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }
}
