using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Mutations;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>
/// The runtime scope guard (ADR-0005; enforced since R1-48, WP2 step 2.6, INV-38): a marked operation runs only inside
/// an open gateway scope that decided it or a capability composing it, a raw primitive only inside an open scope, and a
/// scope the gateway closed authorizes nothing afterwards — not even work that escaped it on a copy of the async flow.
/// </summary>
public class MutationScopeGuardTests
{
    public interface IProbeService
    {
        [MutatingOperation("container.start")]
        Task<string> StartAsync(string id);

        Task<string> ReadAsync(string id);

        [MutatingOperation("container.stop")]
        void Stop(string id);

        [MutatingOperation("config.save")]
        Task SaveAsync<T>(string section, T value);

        [MutatingOperation("image.pull")]
        Task<string> PullAsync(string image);

        [MutatingOperation("config.whitelist")]
        Task<string> SaveWhitelistAsync(string email);

        [MutationPrimitive]
        Task<string> RunAsync(string command);
    }

    public sealed class ProbeService : IProbeService, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Runs { get; private set; }
        public Task<string> StartAsync(string id) { Runs++; return Task.FromResult("started " + id); }
        public Task<string> ReadAsync(string id) => Task.FromResult("read " + id);
        public void Stop(string id) => throw new InvalidOperationException("stop failed for " + id);
        public Task SaveAsync<T>(string section, T value) { Runs++; return Task.CompletedTask; }
        public Task<string> PullAsync(string image) { Runs++; return Task.FromResult("pulled " + image); }
        public Task<string> SaveWhitelistAsync(string email) { Runs++; return Task.FromResult("saved " + email); }
        public Task<string> RunAsync(string command) { Runs++; return Task.FromResult("ran " + command); }
        public void Dispose() => Disposed = true;
    }

    public interface IReadOnlyProbe
    {
        string Read();
    }

    private static MutationScopeMonitor Production() => new(NullLogger<MutationScopeMonitor>.Instance);
    private static MutationScopeMonitor ReportOnly() => MutationScopeMonitor.ReportOnly(NullLogger<MutationScopeMonitor>.Instance);

    // ---------------------------------------------------------------- production: enforced

    [Fact]
    public async Task An_unscoped_mutation_throws_in_production_and_never_runs()
    {
        var inner = new ProbeService();
        var monitor = Production();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(inner, monitor);

        var refused = await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.StartAsync("a"));

        Assert.Equal("container.start", refused.CapabilityId);
        Assert.Null(refused.OpenScope);
        Assert.Equal(0, inner.Runs);
        Assert.Equal(1, Assert.Single(monitor.Snapshot()).Count);   // still recorded for the self-status page
    }

    [Fact]
    public async Task The_production_registration_enforces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProbeService, ProbeService>();
        services.AddSingleton<Whiskers.Services.Mcp.IMcpPermissionService, Whiskers.Services.Mcp.McpPermissionService>();
        services.AddScoped<Whiskers.Services.Auth.ICurrentUserService, NoUser>();
        AddOwnerDependencies(services);
        services.AddMutationGateway();
        services.AddMutationScopeGuard();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.True(provider.GetRequiredService<IMutationScopeMonitor>().Enforce);
        var guarded = provider.GetRequiredService<IProbeService>();
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.StartAsync("a"));
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.SaveAsync("Terminal", new { Enabled = true }));
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.RunAsync("rm -rf /tmp/x"));
    }

    [Fact]
    public async Task A_generic_mutating_call_outside_a_scope_is_refused_too()
    {
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), Production());

        var refused = await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.SaveAsync("Dns", 42));
        Assert.Equal("config.save", refused.CapabilityId);
    }

    [Fact]
    public async Task A_scope_authorizes_its_own_capability_and_what_it_composes_nothing_else()
    {
        var inner = new ProbeService();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(inner, Production());

        using (MutationScope.Begin("container.start", "op-1"))
        {
            Assert.Equal("started a", await guarded.StartAsync("a"));
            Assert.Equal("read a", await guarded.ReadAsync("a"));
            // container.start composes nothing: a pull is not part of that decision.
            var other = await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("nginx"));
            Assert.Equal("container.start", other.OpenScope);
        }

        // app.deploy composes image.pull (MutationCompositions); the decision covers the pull.
        using (MutationScope.Begin("app.deploy", "op-2"))
            Assert.Equal("pulled nginx", await guarded.PullAsync("nginx"));
        Assert.Equal(2, inner.Runs);
    }

    [Fact]
    public async Task A_nested_decision_keeps_the_enclosing_one_in_force()
    {
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), Production());

        // A deployment decided, then a container it touches (ComposeImpact): the pull still belongs to the deployment.
        using (MutationScope.Begin("app.deploy", "op-outer"))
        using (MutationScope.Begin("container.start", "op-inner"))
            Assert.Equal("pulled x", await guarded.PullAsync("x"));
    }

    [Fact]
    public async Task A_primitive_runs_only_inside_an_open_scope()
    {
        var inner = new ProbeService();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(inner, Production());

        var refused = await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.RunAsync("reboot"));
        Assert.StartsWith(MutationScopeProxy<IProbeService>.PrimitiveLabel, refused.CapabilityId);
        Assert.Equal(0, inner.Runs);

        using (MutationScope.Begin("host.exec", "op-1"))
            Assert.Equal("ran uptime", await guarded.RunAsync("uptime"));
    }

    [Fact]
    public async Task The_whitelist_write_is_refused_like_every_other_capability()
    {
        var monitor = Production();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), monitor);

        // R1-48: no pending exception remains (the wizard uses the narrow first-admin methods instead).
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.SaveWhitelistAsync("a@x"));
        Assert.Equal("config.whitelist", Assert.Single(monitor.Snapshot()).CapabilityId);
    }

    [Fact]
    public void Exceptions_pass_through_unwrapped()
    {
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), Production());

        using (MutationScope.Begin("container.stop", "op-1"))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => guarded.Stop("x"));
            Assert.Equal("stop failed for x", ex.Message);
        }
    }

    // ---------------------------------------------------------------- escaped and detached scopes

    [Fact]
    public async Task Work_that_escaped_a_scope_is_refused_once_the_scope_closed()
    {
        var inner = new ProbeService();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(inner, Production());
        var release = new TaskCompletionSource();
        Task<Exception?> escaped;

        using (MutationScope.Begin("container.start", "op-1"))
        {
            // Fire-and-forget from inside the decision: it inherits the flow, then outlives the operation.
            escaped = Task.Run(async () =>
            {
                await release.Task;
                try { await guarded.StartAsync("later"); return null; }
                catch (Exception ex) { return ex; }
            });
        }

        release.SetResult();
        Assert.IsType<MutationScopeViolationException>(await escaped);
        Assert.Equal(0, inner.Runs);
    }

    [Fact]
    public async Task A_timer_started_inside_a_scope_cannot_act_after_it_closed()
    {
        var inner = new ProbeService();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(inner, Production());
        var fired = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource();
        Timer timer;

        using (MutationScope.Begin("container.start", "op-1"))
            timer = new Timer(_ =>
            {
                gate.Task.Wait();
                try { guarded.StartAsync("tick").GetAwaiter().GetResult(); fired.SetResult(null); }
                catch (Exception ex) { fired.SetResult(ex); }
            }, null, 0, Timeout.Infinite);

        gate.SetResult();
        Assert.IsType<MutationScopeViolationException>(await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await timer.DisposeAsync();
        Assert.Equal(0, inner.Runs);
    }

    [Fact]
    public async Task An_escaped_flow_sees_no_scope_and_a_gateway_call_from_it_starts_on_its_own()
    {
        var release = new TaskCompletionSource();
        Task<(MutationScope? Current, bool Authorizes, string? Parent)> escaped;
        MutationScope closed;

        using (closed = MutationScope.Begin("container.start", "op-1"))
            escaped = Task.Run(async () =>
            {
                await release.Task;
                var current = MutationScope.Current;
                var authorizes = MutationScope.Authorizes("container.start");
                using var fresh = MutationScope.Begin("container.stop", "op-2");
                var parentOfFresh = MutationScope.Authorizes("container.start") ? "inherited" : null;
                return (current, authorizes, parentOfFresh);
            });

        release.SetResult();
        var (seen, authorized, parent) = await escaped;
        Assert.True(closed.IsClosed);
        Assert.Null(seen);
        Assert.False(authorized);
        Assert.Null(parent);   // the new scope did not nest into the closed decision
    }

    [Fact]
    public async Task Work_running_while_the_scope_is_open_is_inside_it()
    {
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), Production());

        using (MutationScope.Begin("container.start", "op-1"))
        {
            // Parallel work the delegate awaits before it returns belongs to the decision.
            var results = await Task.WhenAll(Task.Run(() => guarded.StartAsync("a")), Task.Run(() => guarded.StartAsync("b")));
            Assert.Equal(["started a", "started b"], results);
        }
    }

    // ---------------------------------------------------------------- report mode (counting only)

    [Fact]
    public async Task Report_mode_counts_and_lets_the_call_run()
    {
        var monitor = ReportOnly();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), monitor);

        Assert.Equal("started a", await guarded.StartAsync("a"));
        Assert.Equal("started b", await guarded.StartAsync("b"));

        var report = Assert.Single(monitor.Snapshot());
        Assert.Equal("container.start", report.CapabilityId);
        Assert.Equal(2, report.Count);
    }

    [Fact]
    public async Task Reads_and_calls_inside_a_scope_are_not_reported()
    {
        var monitor = Production();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), monitor);

        Assert.Equal("read a", await guarded.ReadAsync("a"));
        using (MutationScope.Begin("container.start", "op-1"))
            Assert.Equal("started a", await guarded.StartAsync("a"));

        Assert.Empty(monitor.Snapshot());
    }

    [Fact]
    public void An_interface_without_marked_methods_is_not_wrapped()
    {
        var plain = new PlainProbe();
        Assert.Same(plain, MutationScopeProxy<IReadOnlyProbe>.Wrap(plain, Production()));
    }

    [Fact]
    public void Scopes_nest_and_restore_the_outer_one()
    {
        using (MutationScope.Begin("container.update", "op-outer"))
        {
            using (MutationScope.Begin("container.stop", "op-inner"))
                Assert.Equal("container.stop", MutationScope.Current!.CapabilityId);
            Assert.Equal("container.update", MutationScope.Current!.CapabilityId);
        }
        Assert.Null(MutationScope.Current);
    }

    /// <summary>What the automation runner needs to resolve owners: roles, whitelist, data paths.</summary>
    private static void AddOwnerDependencies(IServiceCollection services)
    {
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(new Whiskers.Configuration.DataPathOptions(Path.Combine(Path.GetTempPath(), "whiskers-di-" + Guid.NewGuid().ToString("N"))));
        services.AddSingleton<Whiskers.Services.Auth.IRoleService, Whiskers.Services.Auth.RoleService>();
        services.AddSingleton<Whiskers.Services.Auth.IWhitelistService, Whiskers.Services.Auth.WhitelistService>();
        services.AddSingleton<Whiskers.Services.Notifications.INotificationService, Whiskers.Services.Notifications.NoopNotificationService>();   // the interrupted-work report (R1-39)
    }

    [Fact]
    public void The_di_guard_wraps_marked_services_keeps_singletons_single_and_disposes_owned_instances()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ProbeService>();
        services.AddSingleton<IProbeService>(sp => sp.GetRequiredService<ProbeService>());
        services.AddSingleton<IReadOnlyProbe, PlainProbe>();
        services.AddSingleton<Whiskers.Services.Mcp.IMcpPermissionService, Whiskers.Services.Mcp.McpPermissionService>();
        services.AddScoped<Whiskers.Services.Auth.ICurrentUserService, NoUser>();   // the UI runner reads it
        AddOwnerDependencies(services);
        services.AddMutationGateway();
        services.AddMutationScopeGuard();

        ProbeService concrete;
        using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        {
            var guarded = provider.GetRequiredService<IProbeService>();
            concrete = provider.GetRequiredService<ProbeService>();
            Assert.IsAssignableFrom<DispatchProxy>(guarded);
            Assert.Same(guarded, provider.GetRequiredService<IProbeService>());
            Assert.IsType<PlainProbe>(provider.GetRequiredService<IReadOnlyProbe>());
        }
        Assert.True(concrete.Disposed);

        var typed = new ServiceCollection();
        typed.AddLogging();
        typed.AddSingleton<IProbeService, ProbeService>();
        typed.AddSingleton<Whiskers.Services.Mcp.IMcpPermissionService, Whiskers.Services.Mcp.McpPermissionService>();
        typed.AddScoped<Whiskers.Services.Auth.ICurrentUserService, NoUser>();
        AddOwnerDependencies(typed);
        typed.AddMutationGateway();
        typed.AddMutationScopeGuard();
        IProbeService resolved;
        using (var provider = typed.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
            resolved = provider.GetRequiredService<IProbeService>();
        Assert.IsAssignableFrom<DispatchProxy>(resolved);
    }

    private sealed class PlainProbe : IReadOnlyProbe
    {
        public string Read() => "plain";
    }

    [Fact]
    public async Task A_composed_call_must_act_on_the_server_the_decision_was_taken_for()
    {
        // R1-48 review m1: an app.deploy decided for server A composes image.pull — on A, not on B.
        var guarded = MutationScopeProxy<IServerProbe>.Wrap(new ServerProbe(), WithRegistry(defaultServer: "server-a"));
        var request = new MutationRequest("app.deploy",
            new MutationPrincipal(MutationPrincipalKind.Human, "a@x", "a@x", Whiskers.Models.McpPermissionLevels.Admin),
            MutationSurface.Ui, new MutationTarget("host", "server-a", ServerId: "server-a"),
            new Dictionary<string, System.Text.Json.JsonElement>(), OperationId: "op-m1");

        using (MutationScope.Begin("app.deploy", "op-m1", request: request))
        {
            Assert.Equal("pulled x on server-a", await guarded.PullAsync("x", "server-a"));
            Assert.Equal("pulled x on SERVER-A", await guarded.PullAsync("x", "SERVER-A"));   // ids ignore case
            var refused = await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", "server-b"));
            Assert.Equal("app.deploy", refused.OpenScope);
            // Review rounds 2/3: PullAsync declares no omitted-server routing — an omitted server cannot be resolved, so
            // it matches no decided server (fail closed), even though the default here is the decided server.
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", ""));
        }

        // The decided capability itself is the gateway's own resolved target: not matched again here.
        var pull = request with { CapabilityId = "image.pull", OperationId = "op-m1b" };
        using (MutationScope.Begin("image.pull", "op-m1b", request: pull))
            Assert.Equal("pulled x on server-b", await guarded.PullAsync("x", "server-b"));
    }

    // A monitor whose server registry has server-a and server-b, with <paramref name="defaultServer"/> as the default.
    private static MutationScopeMonitor WithRegistry(string defaultServer) => WithServers(
        new Whiskers.Models.ServerConfig { Id = "server-a", Name = "Alpha", IsDefault = defaultServer == "server-a" },
        new Whiskers.Models.ServerConfig { Id = "server-b", Name = "Beta", IsDefault = defaultServer == "server-b" });

    private static MutationScopeMonitor WithServers(params Whiskers.Models.ServerConfig[] servers) =>
        WithRegistryService(new FakeServerConfig(servers));

    private static MutationScopeMonitor WithRegistryService(Whiskers.Services.ServerConfig.IServerConfigService registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        return new MutationScopeMonitor(NullLogger<MutationScopeMonitor>.Instance, services: services.BuildServiceProvider());
    }

    [Fact]
    public async Task An_omitted_server_resolves_by_the_operations_own_routing_not_the_docker_default()
    {
        // Review round 3: volume.restore decided for B; B is the configured default, Whiskers' own host "local" is A.
        // The volume service routes an omitted server to "local" (its declared fallback), so the nested safety backup
        // with serverId: null acts on A — refused before it runs.
        var inner = new RecordingVolumes();
        var volumes = MutationScopeProxy<Whiskers.Services.Backup.IVolumeBackupService>.Wrap(inner, WithServers(
            new Whiskers.Models.ServerConfig { Id = "local", Name = "A" },
            new Whiskers.Models.ServerConfig { Id = "server-b", Name = "B", IsDefault = true }));
        var restoreOnB = DeployOn("server-b", "op-r3a") with { CapabilityId = "volume.restore" };
        using (MutationScope.Begin("volume.restore", "op-r3a", request: restoreOnB))
        {
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => volumes.BackupVolumeAsync("data", "app", serverId: null));
            Assert.Equal(0, inner.Backups);
            await volumes.BackupVolumeAsync("data", "app", serverId: "server-b");   // named: the decided server
            Assert.Equal(1, inner.Backups);
        }

        var restoreOnLocal = DeployOn("local", "op-r3b") with { CapabilityId = "volume.restore" };
        using (MutationScope.Begin("volume.restore", "op-r3b", request: restoreOnLocal))
            await volumes.BackupVolumeAsync("data", "app", serverId: null);       // omitted = local = the decided server
        Assert.Equal(2, inner.Backups);
    }

    [Fact]
    public async Task Unresolvable_server_identities_match_nothing()
    {
        // Review round 3: an unknown name, a missing registry, a failing registry and an unresolvable decision server all
        // fail closed — nothing runs.
        var request = DeployOn("ghost", "op-r3c");

        var unknown = new ServerProbe();
        var guarded = MutationScopeProxy<IServerProbe>.Wrap(unknown, WithRegistry(defaultServer: "server-a"));
        using (MutationScope.Begin("app.deploy", "op-r3c", request: request))
        {
            // Decision and call name the same unknown server: no match.
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", "ghost"));
            // The decision's server is unresolvable: not even the default server matches it.
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullOnDefaultAsync("x"));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", "server-a"));
        }
        Assert.Equal(0, unknown.Calls);

        var noRegistry = new ServerProbe();
        var bare = MutationScopeProxy<IServerProbe>.Wrap(noRegistry, Production());
        using (MutationScope.Begin("app.deploy", "op-r3d", request: DeployOn("server-a", "op-r3d")))
        {
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => bare.PullAsync("x", "server-a"));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => bare.PullOnDefaultAsync("x"));
        }
        Assert.Equal(0, noRegistry.Calls);

        var failing = new ServerProbe();
        var broken = MutationScopeProxy<IServerProbe>.Wrap(failing, WithRegistryService(new ThrowingRegistry()));
        using (MutationScope.Begin("app.deploy", "op-r3e", request: DeployOn("server-a", "op-r3e")))
        {
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => broken.PullAsync("x", "server-a"));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => broken.PullOnDefaultAsync("x"));
        }
        Assert.Equal(0, failing.Calls);
    }

    [Fact]
    public async Task A_decision_server_that_cannot_be_resolved_is_not_compared_by_its_raw_text()
    {
        // The registry fails for the decision's lookup but resolves the call's default to the same id: the raw text of an
        // unresolved decision must not stand in for its identity.
        var inner = new ServerProbe();
        var guarded = MutationScopeProxy<IServerProbe>.Wrap(inner, WithRegistryService(new LookupFailsRegistry()));
        using (MutationScope.Begin("app.deploy", "op-r3f", request: DeployOn("server-a", "op-r3f")))
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullOnDefaultAsync("x"));
        Assert.Equal(0, inner.Calls);
    }

    // Named lookups fail; the default server (server-a) still resolves.
    private sealed class LookupFailsRegistry : Whiskers.Services.ServerConfig.IServerConfigService
    {
        private static readonly Whiskers.Models.ServerConfig A = new() { Id = "server-a", Name = "Alpha", IsDefault = true };
        public bool IsInitialized => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<Whiskers.Models.ServerConfig> GetServers() => [A];
        public List<Whiskers.Models.ServerConfig> GetEnabledServers() => [A];
        public Whiskers.Models.ServerConfig? GetServer(string serverId) => throw new IOException("lookup failed");
        public Whiskers.Models.ServerConfig? GetDefaultServer() => A;
        public bool SupportsTerminal(string? serverId) => false;
        public Task AddServerAsync(Whiskers.Models.ServerConfig server) => throw new NotSupportedException();
        public Task UpdateServerAsync(Whiskers.Models.ServerConfig server) => throw new NotSupportedException();
        public Task RemoveServerAsync(string serverId) => throw new NotSupportedException();
        public Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData) => throw new NotSupportedException();
        public string? GetSshKeyPath(Whiskers.Models.ServerConfig server) => null;
        public Task DeleteSshKeyAsync(string serverId) => throw new NotSupportedException();
        public Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef) => throw new NotSupportedException();
    }

    private sealed class ThrowingRegistry : Whiskers.Services.ServerConfig.IServerConfigService
    {
        public bool IsInitialized => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<Whiskers.Models.ServerConfig> GetServers() => throw new IOException("registry down");
        public List<Whiskers.Models.ServerConfig> GetEnabledServers() => throw new IOException("registry down");
        public Whiskers.Models.ServerConfig? GetServer(string serverId) => throw new IOException("registry down");
        public Whiskers.Models.ServerConfig? GetDefaultServer() => throw new IOException("registry down");
        public bool SupportsTerminal(string? serverId) => false;
        public Task AddServerAsync(Whiskers.Models.ServerConfig server) => throw new NotSupportedException();
        public Task UpdateServerAsync(Whiskers.Models.ServerConfig server) => throw new NotSupportedException();
        public Task RemoveServerAsync(string serverId) => throw new NotSupportedException();
        public Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData) => throw new NotSupportedException();
        public string? GetSshKeyPath(Whiskers.Models.ServerConfig server) => null;
        public Task DeleteSshKeyAsync(string serverId) => throw new NotSupportedException();
        public Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef) => throw new NotSupportedException();
    }

    private sealed class RecordingVolumes : Whiskers.Services.Backup.IVolumeBackupService
    {
        public int Backups;
        public Task<Whiskers.Models.VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null, string? notes = null)
        {
            Interlocked.Increment(ref Backups);
            return Task.FromResult(new Whiskers.Models.VolumeBackupEntity());
        }
        public Task RestoreVolumeAsync(string backupId, string? targetVolume = null) => Task.CompletedTask;
        public Task<List<Whiskers.Models.VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null) => Task.FromResult(new List<Whiskers.Models.VolumeBackupEntity>());
        public Task DeleteBackupAsync(string backupId) => Task.CompletedTask;
        public Task<List<string>> ListVolumesAsync(string? serverId = null) => Task.FromResult(new List<string>());
    }

    [Fact]
    public void The_registered_monitor_resolves_through_the_registered_server_registry()
    {
        // The production registration gives the monitor the registry (resolved on use).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
            new Whiskers.Models.ServerConfig { Id = "server-a", Name = "Alpha", IsDefault = true }));
        services.AddSingleton<IMutationScopeMonitor, MutationScopeMonitor>();
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IMutationScopeMonitor>();
        Assert.Equal("server-a", monitor.ResolveServer(null, new OmittedServerAttribute()));
        Assert.Equal("server-a", monitor.ResolveServer("ALPHA"));
        Assert.Null(monitor.ResolveServer("ghost"));
        Assert.Null(monitor.ResolveServer(null));                     // no declaration: unknown
    }

    private static MutationRequest DeployOn(string serverId, string op) => new("app.deploy",
        new MutationPrincipal(MutationPrincipalKind.Human, "a@x", "a@x", Whiskers.Models.McpPermissionLevels.Admin),
        MutationSurface.Ui, new MutationTarget("host", serverId, ServerId: serverId),
        new Dictionary<string, System.Text.Json.JsonElement>(), OperationId: op);

    [Fact]
    public async Task A_composed_call_that_omits_its_server_acts_on_the_default_and_is_matched_against_it()
    {
        // R1-48 review round 2: app.deploy decided for A; the pull names no server, and the transport's default is B.
        var inner = new ServerProbe();
        var guarded = MutationScopeProxy<IServerProbe>.Wrap(inner, WithRegistry(defaultServer: "server-b"));
        using (MutationScope.Begin("app.deploy", "op-r2a", request: DeployOn("server-a", "op-r2a")))
        {
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullOnDefaultAsync("x"));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", null!));
            Assert.Equal(0, inner.Calls);                                         // refused before it ran
            // A named server is compared canonically: the display name of A is A.
            Assert.Equal("pulled x on Alpha", await guarded.PullAsync("x", "Alpha"));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => guarded.PullAsync("x", "Beta"));
        }

        // The same pull is allowed when the default is the decided server.
        var onA = MutationScopeProxy<IServerProbe>.Wrap(new ServerProbe(), WithRegistry(defaultServer: "server-a"));
        using (MutationScope.Begin("app.deploy", "op-r2b", request: DeployOn("server-a", "op-r2b")))
            Assert.Equal("pulled x on default", await onA.PullOnDefaultAsync("x"));
    }

    [Fact]
    public async Task A_serverless_composed_operation_is_not_matched_by_server()
    {
        // An operation without a server parameter has no server to compare: the decision covers it as before.
        var guarded = MutationScopeProxy<IServerProbe>.Wrap(new ServerProbe(), WithRegistry(defaultServer: "server-b"));
        using (MutationScope.Begin("app.deploy", "op-r2c", request: DeployOn("server-a", "op-r2c")))
            Assert.Equal("created", await guarded.CreateAsync());
    }

    public interface IServerProbe
    {
        [MutatingOperation("image.pull")]
        Task<string> PullAsync(string image, string serverId);

        [MutatingOperation("image.pull")]
        [OmittedServer]
        Task<string> PullOnDefaultAsync(string image, string? serverId = null);

        [MutatingOperation("container.create")]
        Task<string> CreateAsync();
    }

    private sealed class ServerProbe : IServerProbe
    {
        public int Calls;
        public Task<string> PullAsync(string image, string serverId)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult($"pulled {image} on {serverId}");
        }

        public Task<string> PullOnDefaultAsync(string image, string? serverId = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult($"pulled {image} on {serverId ?? "default"}");
        }

        public Task<string> CreateAsync() => Task.FromResult("created");
    }
}

internal sealed class NoUser : Whiskers.Services.Auth.ICurrentUserService
{
    public Task<string?> GetEmailAsync() => Task.FromResult<string?>(null);
    public Task<Whiskers.Models.AppRole> GetRoleAsync() => Task.FromResult(Whiskers.Models.AppRole.Viewer);
    public Task<bool> HasRoleAsync(Whiskers.Models.AppRole required) => Task.FromResult(false);
}
