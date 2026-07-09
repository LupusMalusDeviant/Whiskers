using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whiskers.Mcp.Tools;
using Whiskers.Modules;
using Whiskers.Modules.Scheduler;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the Scheduler module move. The first extracted module with BOTH a nav entry
/// and MCP tools, so these cover: the module metadata (nav "tasks" + SchedulerTools), that ConfigureServices
/// registers the scheduler service graph (ISchedulerService + ITaskExecutor + the hosted background service),
/// and the ModuleCatalog enable/disable gate. Full-graph DI resolution is covered by the app boot-gate
/// (TaskExecutor pulls Docker/DB/backup Core services, so it isn't reconstructed here).</summary>
public class SchedulerModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_tasks_nav_entry()
    {
        var nav = Assert.Single(new SchedulerModule().NavItems);
        Assert.Equal("tasks", nav.Href);
        Assert.Equal("Automatisierung", nav.Group);
        Assert.Equal(310, nav.Order);
    }

    [Fact]
    public void Exposes_the_scheduler_mcp_tools()
    {
        Assert.Equal(new[] { typeof(SchedulerTools) }, new SchedulerModule().McpToolTypes);
    }

    [Fact]
    public void Tasks_nav_entry_moved_out_of_the_pseudo_module()
    {
        // The "tasks" entry must live in exactly one place now — the module, not the transitional bucket.
        Assert.DoesNotContain(new AllInOnePseudoModule().NavItems, n => n.Href == "tasks");
        Assert.DoesNotContain(typeof(SchedulerTools), new AllInOnePseudoModule().McpToolTypes);
    }

    // --- Registration shape (no heavy resolve; the app boot-gate resolves the full graph) -----------------

    [Fact]
    public void ConfigureServices_registers_the_scheduler_service_graph()
    {
        var services = new ServiceCollection();
        new SchedulerModule().ConfigureServices(services, Config());

        // ISchedulerService + ITaskExecutor forwarders, the concrete singleton, and the hosted background loop.
        Assert.Contains(services, d => d.ServiceType == typeof(ISchedulerService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITaskExecutor) && d.ImplementationType == typeof(TaskExecutor));
        Assert.Contains(services, d => d.ServiceType == typeof(SchedulerService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "scheduler");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:scheduler:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "scheduler");
        // Other modules are unaffected by the scheduler flag.
        Assert.Contains(enabled, m => m.Id == "all-in-one");
        Assert.Contains(enabled, m => m.Id == "notifications");
    }
}
