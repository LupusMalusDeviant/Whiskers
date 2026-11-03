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
    public async Task The_capability_pending_a_decision_is_recorded_not_refused()
    {
        var monitor = Production();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), monitor);

        Assert.Equal("saved a@x", await guarded.SaveWhitelistAsync("a@x"));
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
}

internal sealed class NoUser : Whiskers.Services.Auth.ICurrentUserService
{
    public Task<string?> GetEmailAsync() => Task.FromResult<string?>(null);
    public Task<Whiskers.Models.AppRole> GetRoleAsync() => Task.FromResult(Whiskers.Models.AppRole.Viewer);
    public Task<bool> HasRoleAsync(Whiskers.Models.AppRole required) => Task.FromResult(false);
}
