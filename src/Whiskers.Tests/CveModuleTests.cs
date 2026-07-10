using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whiskers.Mcp.Tools;
using Whiskers.Modules;
using Whiskers.Modules.Cve;
using Whiskers.Services.Cve;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 §3.5 — the CVE module move. Covers the metadata (nav "cves" + CveTools, which
/// is dedicated so it moves with the module), the registrations (findings store, age store, scanners, hosted
/// monitor), the ModuleCatalog gate, and the soft-dependency no-ops: the findings store + monitor are consumed
/// by Core pages (Dashboard/ContainerDetail/Settings), so Core no-op defaults must resolve when the module is
/// off and be overridden when on. (C8 service-locator removal in CveMonitorService is deferred to a focused
/// follow-up — the extraction itself is byte-identical.)</summary>
public class CveModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_cves_nav_and_the_cve_tools()
    {
        var module = new CveModule();
        Assert.Equal("cve", module.Id);
        var nav = Assert.Single(module.NavItems);
        Assert.Equal("cves", nav.Href);
        Assert.Equal("Übersicht", nav.Group);
        Assert.Equal(new[] { typeof(CveTools) }, module.McpToolTypes);
    }

    [Fact]
    public void Cves_nav_and_tools_moved_out_of_the_pseudo_module()
    {
        Assert.DoesNotContain(new AllInOnePseudoModule().NavItems, n => n.Href == "cves");
        Assert.DoesNotContain(typeof(CveTools), new AllInOnePseudoModule().McpToolTypes);
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_stores_scanners_and_hosted_monitor()
    {
        var services = new ServiceCollection();
        new CveModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(ICveFindingsStore) && d.ImplementationType == typeof(CveFindingsStore));
        Assert.Contains(services, d => d.ServiceType == typeof(ICveAgeStore) && d.ImplementationType == typeof(CveAgeStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IOsCveScanner) && d.ImplementationType == typeof(OsCveScanner));
        Assert.Contains(services, d => d.ServiceType == typeof(ITrivyScanner) && d.ImplementationType == typeof(TrivyScanner));
        Assert.Contains(services, d => d.ServiceType == typeof(ICveMonitorService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Disabled_module_keeps_the_cve_noops()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICveFindingsStore, NoopCveFindingsStore>();
        services.AddSingleton<ICveMonitorService, NoopCveMonitorService>();
        services.AddSingleton<ICveAgeStore, NoopCveAgeStore>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopCveFindingsStore>(sp.GetRequiredService<ICveFindingsStore>());
        Assert.IsType<NoopCveMonitorService>(sp.GetRequiredService<ICveMonitorService>());
        Assert.IsType<NoopCveAgeStore>(sp.GetRequiredService<ICveAgeStore>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noops_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICveFindingsStore, NoopCveFindingsStore>();
        services.AddSingleton<ICveAgeStore, NoopCveAgeStore>();
        new CveModule().ConfigureServices(services, Config());
        Assert.Equal(typeof(CveFindingsStore), services.Last(d => d.ServiceType == typeof(ICveFindingsStore)).ImplementationType);
        Assert.Equal(typeof(CveAgeStore), services.Last(d => d.ServiceType == typeof(ICveAgeStore)).ImplementationType);
    }

    // --- No-op semantics ---------------------------------------------------------------------------------

    [Fact]
    public async Task Noop_reads_return_empty()
    {
        Assert.Empty(new NoopCveFindingsStore().GetAll());
        Assert.Empty(await new NoopCveAgeStore().GetFirstSeenAsync());
        await new NoopCveMonitorService().RunOneCycleAsync(); // completes without error
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "cve");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:cve:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "cve");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
