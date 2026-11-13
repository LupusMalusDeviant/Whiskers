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

using Whiskers.Services.Persistence;

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
        Services.AddSingleton(EmptyClientCredentialStore());
        Services.AddSingleton(Null<Whiskers.Services.Certificates.IMtlsCertificateService>());
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
        DurableFile.FailingWrites[Path.GetFullPath(_path)] = 0;   // the retry cannot write servers.json
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
    public async Task A_client_built_during_a_rotation_reads_the_new_kubeconfig()
    {
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", "yaml-A");
        await Persist(page);
        var oldKey = KubernetesClientCache.KubeconfigKeyOf(_servers.GetServer(server.Id)!);

        // The build captured the record naming A; the rotation to B publishes and removes A before the read.
        _vault.BeforeRead = key =>
        {
            if (key != oldKey) return;
            _vault.BeforeRead = null;
            Set(page, "_kubeconfigYaml", "yaml-B");
            Persist(page).GetAwaiter().GetResult();
        };

        var cache = new KubernetesClientCache(_servers, _vault);
        Assert.Equal("yaml-B", cache.ReadKubeconfig(server.Id).Yaml);
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

    [Fact]
    public async Task A_kubeconfig_with_an_exec_plugin_is_refused_before_it_reaches_the_vault()
    {
        // R1-42: the page answers early; KubernetesClientCache refuses it again when it builds a client.
        var page = Render<Servers>();
        var server = new ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        Set(page, "_form", server);
        Set(page, "_kubeconfigYaml", KubeconfigExecutionTests.ExecKubeconfig("/usr/bin/true", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));

        Assert.Empty(_vault.Keys);
        Assert.Null(_servers.GetServer(server.Id));
    }

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/pwned", "host.example.com", 22)]
    [InlineData("root", "-oProxyCommand=touch /tmp/pwned", 22)]
    [InlineData("root", "host.example.com", 0)]
    public async Task An_unsafe_ssh_target_is_refused_before_the_server_is_saved(string user, string host, int port)
    {
        // R1-42: the executor, tunnel and terminal refuse it anyway; the page says so while the dialog is open.
        var page = Render<Servers>();
        var server = new ServerConfig
        {
            Name = "edge", ConnectionType = ConnectionType.SSH, SshUser = user, SshHost = host, SshPort = port,
        };
        Set(page, "_form", server);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));

        Assert.Null(_servers.GetServer(server.Id));
    }

    /// <summary>Forwards to the real registry and records SSH key uploads instead of sealing them (no vault here).</summary>
    public class KeyUploadRecorder : DispatchProxy
    {
        public IServerConfigService Inner { get; set; } = null!;
        public List<string> Uploads { get; } = [];

        public static (IServerConfigService Service, KeyUploadRecorder Recorder) Over(IServerConfigService inner)
        {
            var service = DispatchProxy.Create<IServerConfigService, KeyUploadRecorder>();
            var recorder = (KeyUploadRecorder)(object)service;
            recorder.Inner = inner;
            return (service, recorder);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IServerConfigService.SaveSshKeyAsync))
            {
                lock (Uploads) Uploads.Add((string)args![0]!);
                return Task.CompletedTask;
            }
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    // The recorder never reads the key; any bytes stand for an uploaded key file.
    private static readonly byte[] UploadedKey = "placeholder"u8.ToArray();

    [Fact]
    public async Task A_key_upload_while_an_onboarding_holds_the_host_writes_nothing()
    {
        // INV-59 (#187, Codex round 6): the SSH key upload writes the whole server record. It runs under the per-host
        // lock with the rest of the save, so while an onboarding holds the host nothing is written, the key included.
        var (service, recorder) = KeyUploadRecorder.Over(_servers);
        Services.AddSingleton(service);
        var page = Render<Servers>();
        var server = new ServerConfig
        {
            Name = "edge", ConnectionType = ConnectionType.SSH, SshUser = "root", SshHost = "host.example.com", SshPort = 22
        };
        Set(page, "_form", server);
        await Persist(page);

        Set(page, "_editingServer", _servers.GetServer(server.Id)!.Clone());
        var edited = _servers.GetServer(server.Id)!.Clone();
        edited.Name = "edge (neu)";
        Set(page, "_form", edited);
        Set(page, "_sshKeyData", UploadedKey);
        Set(page, "_sshKeyFileName", "id_ed25519");

        var gate = Services.GetRequiredService<Whiskers.Services.Certificates.IMtlsClientCredentialStore>().HostLock(server.Id);
        Assert.True(await gate.WaitAsync(0));   // a running onboarding
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));
            Assert.Empty(recorder.Uploads);
            Assert.Equal("edge", _servers.GetServer(server.Id)!.Name);
        }
        finally { gate.Release(); }

        await Persist(page);
        Assert.Equal([server.Id], recorder.Uploads);
        Assert.Equal("edge (neu)", _servers.GetServer(server.Id)!.Name);
    }

    [Fact]
    public async Task A_dialog_for_a_removed_server_writes_nothing_to_a_server_named_like_its_id()
    {
        // Codex round 7: the dialog for "edge" stays open, "edge" is removed, and another server is named like its id
        // while an onboarding holds that server. Saving the stale dialog with a key upload writes nothing anywhere.
        var (service, recorder) = KeyUploadRecorder.Over(_servers);
        Services.AddSingleton(service);
        var page = Render<Servers>();
        var removed = new ServerConfig
        {
            Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH, SshUser = "root", SshHost = "host.example.com", SshPort = 22
        };
        await _servers.AddServerAsync(removed);
        Set(page, "_editingServer", removed.Clone());
        Set(page, "_form", removed.Clone());
        Set(page, "_sshKeyData", UploadedKey);
        Set(page, "_sshKeyFileName", "id_ed25519");

        await _servers.RemoveServerAsync("edge");
        await _servers.AddServerAsync(new ServerConfig
        {
            Id = "zirkus", Name = "edge", ConnectionType = ConnectionType.SSH, SshUser = "root", SshHost = "other.example.com", SshPort = 22
        });
        var gate = Services.GetRequiredService<Whiskers.Services.Certificates.IMtlsClientCredentialStore>().HostLock("zirkus");
        Assert.True(await gate.WaitAsync(0));   // an onboarding of the other server
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));
        }
        finally { gate.Release(); }

        Assert.Empty(recorder.Uploads);
        Assert.DoesNotContain(_servers.GetServers(), s => s.Id == "edge");   // not written back
        Assert.Equal("other.example.com", _servers.GetServer("zirkus")!.SshHost);
    }

    [Fact]
    public async Task A_key_upload_for_a_new_server_waits_for_no_step_holding_its_host()
    {
        var (service, recorder) = KeyUploadRecorder.Over(_servers);
        Services.AddSingleton(service);
        var page = Render<Servers>();
        var server = new ServerConfig
        {
            Name = "edge", ConnectionType = ConnectionType.SSH, SshUser = "root", SshHost = "host.example.com", SshPort = 22
        };
        Set(page, "_form", server);
        Set(page, "_sshKeyData", UploadedKey);
        Set(page, "_sshKeyFileName", "id_ed25519");

        var gate = Services.GetRequiredService<Whiskers.Services.Certificates.IMtlsClientCredentialStore>().HostLock(server.Id);
        Assert.True(await gate.WaitAsync(0));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Persist(page));
            Assert.Empty(recorder.Uploads);
        }
        finally { gate.Release(); }
    }

    private static Task Persist(IRenderedComponent<Servers> page)
    {
        var method = typeof(Servers).GetMethod("PersistServerCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var arguments = typeof(Servers).GetMethod("SaveArguments", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Contract 0012: the core acts on the server the gateway resolved for the save.
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance,
            [TestTargets.Of<ResolvedTarget>("server.save", arguments.Invoke(page.Instance, null)!)])!);
    }

    private static void Set(IRenderedComponent<Servers> page, string field, object? value) =>
        typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page.Instance, value);

    private static T Get<T>(IRenderedComponent<Servers> page, string field) =>
        (T)typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    /// <summary>A do-nothing implementation of <typeparamref name="T"/>: default values and completed tasks.</summary>
    private static T Null<T>() where T : class => NullProxy<T>.Create();

    /// <summary>R1-44: the page shows each host's client credential; a store over an empty data directory has none.</summary>
    internal static Whiskers.Services.Certificates.IMtlsClientCredentialStore EmptyClientCredentialStore() =>
        new Whiskers.Services.Certificates.MtlsClientCredentialStore(Null<Whiskers.Services.Server.IHostCommandExecutor>(),
            NullLogger<Whiskers.Services.Certificates.MtlsClientCredentialStore>.Instance,
            new DataPathOptions(Path.Combine(Path.GetTempPath(), "whiskers-empty-" + Guid.NewGuid().ToString("N"))));

    /// <summary>The shared <see cref="Whiskers.Tests.NullProxy{T}"/>, kept under this name for the tests that still
    /// reach it here.</summary>
    public class NullProxy<T> : Whiskers.Tests.NullProxy<T> where T : class
    {
        public static new T Create() => DispatchProxy.Create<T, NullProxy<T>>();
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }

    private sealed class PassThrough : IUiMutationRunner
    {
        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);
        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);
    }

    private sealed class MemoryVault : IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        public IEnumerable<string> Keys => _secrets.Keys;
        public Action<string>? BeforeRead { get; set; }
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => _secrets.Keys.Select(k => new VaultEntry { Key = k }).ToList();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) { _secrets[key] = plainValue; return Task.CompletedTask; }
        public string? GetSecret(string key)
        {
            BeforeRead?.Invoke(key);
            return _secrets.TryGetValue(key, out var v) ? v : null;
        }
        public Task DeleteSecretAsync(string key) { _secrets.TryRemove(key, out _); return Task.CompletedTask; }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }
}
