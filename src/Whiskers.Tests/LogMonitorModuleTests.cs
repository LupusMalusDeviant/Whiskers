using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whiskers.Mcp.Tools;
using Whiskers.Modules;
using Whiskers.Modules.LogMonitor;
using Whiskers.Services.LogMonitor;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the LogMonitor module move. Covers the module metadata (nav "logs" +
/// LogTools), that ConfigureServices registers the search service + hosted monitor, the ModuleCatalog gate,
/// and the soft-dependency no-op: the Core AI-triggers page consumes ILogMonitorService, so a
/// NoopLogMonitorService default must resolve when the module is off and be overridden by the real hosted
/// monitor when on. Full-graph DI resolution (LogMonitorService pulls Docker/notification Core services) is
/// covered by the app boot-gate.</summary>
public class LogMonitorModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_logs_nav_entry()
    {
        var nav = Assert.Single(new LogMonitorModule().NavItems);
        Assert.Equal("logs", nav.Href);
        Assert.Equal("Übersicht", nav.Group);
        Assert.Equal(40, nav.Order);
    }

    [Fact]
    public void Exposes_the_log_mcp_tools()
    {
        Assert.Equal(new[] { typeof(LogTools) }, new LogMonitorModule().McpToolTypes);
    }

    [Fact]
    public void Logs_nav_and_tools_moved_out_of_the_pseudo_module()
    {
        Assert.DoesNotContain(new AllInOnePseudoModule().NavItems, n => n.Href == "logs");
        Assert.DoesNotContain(typeof(LogTools), new AllInOnePseudoModule().McpToolTypes);
    }

    // --- Registration shape ------------------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_search_and_the_hosted_monitor()
    {
        var services = new ServiceCollection();
        new LogMonitorModule().ConfigureServices(services, Config());

        Assert.Contains(services, d => d.ServiceType == typeof(ILogSearchService) && d.ImplementationType == typeof(LogSearchService));
        Assert.Contains(services, d => d.ServiceType == typeof(LogMonitorService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILogMonitorService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    // --- Soft-dependency no-op (ILogMonitorService) ------------------------------------------------------

    [Fact]
    public void Disabled_module_keeps_the_noop_log_monitor()
    {
        // Mirrors Program.cs: the Noop is the Core default when the module's ConfigureServices doesn't run.
        var services = new ServiceCollection();
        services.AddSingleton<ILogMonitorService, NoopLogMonitorService>();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<NoopLogMonitorService>(sp.GetRequiredService<ILogMonitorService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noop_by_last_registration()
    {
        // Core registers the Noop first, then the module registers the real forwarder — which must win.
        var services = new ServiceCollection();
        services.AddSingleton<ILogMonitorService, NoopLogMonitorService>();
        new LogMonitorModule().ConfigureServices(services, Config());

        var last = services.Last(d => d.ServiceType == typeof(ILogMonitorService));
        Assert.NotEqual(typeof(NoopLogMonitorService), last.ImplementationType); // module forwarder (factory), not the Noop
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "logmonitor");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:logmonitor:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "logmonitor");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
