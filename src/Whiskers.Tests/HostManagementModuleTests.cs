using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Modules;
using Whiskers.Modules.HostManagement;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the HostManagement module move (firewall/nginx/systemd/ssl as one module).
/// Covers the metadata (no nav, no MCP tools — its tools live in the mixed, Core-resident ServerTools), the
/// four service registrations, the ModuleCatalog gate, and the soft-dependency no-ops. Because ServerTools
/// (Core) and the four pages consume these services, Core registers no-op defaults that must resolve when the
/// module is off and be overridden when on. The no-op mutations return a FAILED CommandResult (never fake a
/// success — e.g. a silent "firewall rule added" would be a security footgun).</summary>
public class HostManagementModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Has_no_nav_and_no_mcp_tools()
    {
        var module = new HostManagementModule();
        Assert.Equal("host-management", module.Id);
        Assert.Empty(module.NavItems);
        Assert.Empty(module.McpToolTypes);
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_four_real_services()
    {
        var services = new ServiceCollection();
        new HostManagementModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(IFirewallService) && d.ImplementationType == typeof(FirewallService));
        Assert.Contains(services, d => d.ServiceType == typeof(INginxService) && d.ImplementationType == typeof(NginxService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISystemdService) && d.ImplementationType == typeof(SystemdService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISslCertService) && d.ImplementationType == typeof(SslCertService));
    }

    [Fact]
    public void Disabled_module_keeps_the_four_noops()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFirewallService, NoopFirewallService>();
        services.AddSingleton<INginxService, NoopNginxService>();
        services.AddSingleton<ISystemdService, NoopSystemdService>();
        services.AddSingleton<ISslCertService, NoopSslCertService>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopFirewallService>(sp.GetRequiredService<IFirewallService>());
        Assert.IsType<NoopNginxService>(sp.GetRequiredService<INginxService>());
        Assert.IsType<NoopSystemdService>(sp.GetRequiredService<ISystemdService>());
        Assert.IsType<NoopSslCertService>(sp.GetRequiredService<ISslCertService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noops_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFirewallService, NoopFirewallService>();
        services.AddSingleton<INginxService, NoopNginxService>();
        services.AddSingleton<ISystemdService, NoopSystemdService>();
        services.AddSingleton<ISslCertService, NoopSslCertService>();
        new HostManagementModule().ConfigureServices(services, Config());
        Assert.Equal(typeof(FirewallService), services.Last(d => d.ServiceType == typeof(IFirewallService)).ImplementationType);
        Assert.Equal(typeof(SslCertService), services.Last(d => d.ServiceType == typeof(ISslCertService)).ImplementationType);
    }

    // --- No-op semantics (fail, don't fake) --------------------------------------------------------------

    [Fact]
    public async Task Noop_mutations_return_a_failed_result_not_a_fake_success()
    {
        Assert.False((await new NoopFirewallService().AddRuleAsync("s", "80")).Success);
        Assert.False((await new NoopFirewallService().SetStatusAsync("s", true)).Success);
        Assert.False((await new NoopNginxService().ReloadAsync("s")).Success);
        Assert.False((await new NoopSystemdService().RestartAsync("s", "nginx")).Success);
        Assert.False((await new NoopSslCertService().RenewAsync("s", "example.com")).Success);
    }

    [Fact]
    public async Task Noop_reads_return_empty()
    {
        Assert.Empty((await new NoopFirewallService().GetStatusAsync("s")).Rules);
        Assert.Empty(await new NoopNginxService().ListSitesAsync("s"));
        Assert.Empty(await new NoopSystemdService().ListServicesAsync("s"));
        Assert.Empty(await new NoopSslCertService().ListCertificatesAsync("s"));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "host-management");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:host-management:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "host-management");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
