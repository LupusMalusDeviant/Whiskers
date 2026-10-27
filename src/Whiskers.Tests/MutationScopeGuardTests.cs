using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Mutations;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>The report-mode scope guard (ADR-0005 step 2.0): it observes, and changes no call.</summary>
public class MutationScopeGuardTests
{
    public interface IProbeService
    {
        [MutatingOperation("container.start")]
        Task<string> StartAsync(string id);

        Task<string> ReadAsync(string id);

        [MutatingOperation("container.stop")]
        void Stop(string id);
    }

    public sealed class ProbeService : IProbeService, IDisposable
    {
        public bool Disposed { get; private set; }
        public Task<string> StartAsync(string id) => Task.FromResult("started " + id);
        public Task<string> ReadAsync(string id) => Task.FromResult("read " + id);
        public void Stop(string id) => throw new InvalidOperationException("stop failed for " + id);
        public void Dispose() => Disposed = true;
    }

    public interface IReadOnlyProbe
    {
        string Read();
    }

    private static MutationScopeMonitor Monitor() => new(NullLogger<MutationScopeMonitor>.Instance);

    [Fact]
    public async Task A_mutating_call_outside_a_scope_is_reported_and_still_runs()
    {
        var monitor = Monitor();
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
        var monitor = Monitor();
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), monitor);

        Assert.Equal("read a", await guarded.ReadAsync("a"));
        using (MutationScope.Begin("container.start", "op-1"))
            Assert.Equal("started a", await guarded.StartAsync("a"));

        Assert.Empty(monitor.Snapshot());
    }

    [Fact]
    public void Exceptions_pass_through_unwrapped()
    {
        var guarded = MutationScopeProxy<IProbeService>.Wrap(new ProbeService(), Monitor());

        var ex = Assert.Throws<InvalidOperationException>(() => guarded.Stop("x"));
        Assert.Equal("stop failed for x", ex.Message);
    }

    [Fact]
    public void An_interface_without_marked_methods_is_not_wrapped()
    {
        var plain = new PlainProbe();
        Assert.Same(plain, MutationScopeProxy<IReadOnlyProbe>.Wrap(plain, Monitor()));
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
