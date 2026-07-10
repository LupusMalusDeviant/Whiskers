using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>RoadToSAP §6 DoD — the module matrix. Proves the enable/disable gate at the extremes ("all on"
/// defaults, "all off", "only Core"), that the example module is opt-in, catalog consistency (unique ids +
/// nav hrefs), and that every module's ConfigureServices runs without throwing. This is the DI-level matrix
/// (module discovery + registration sanity); the full-app WebApplicationFactory boot in each mode is covered
/// per-module today by the Development/ValidateOnBuild boot-gate and is a follow-up once the Agent module +
/// integration-test infra land.</summary>
public class ModuleMatrixTests
{
    private static IConfiguration Cfg(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // Every module the catalog knows about — including the disabled-by-default HelloWorld example.
    private static IReadOnlyList<IWhiskersModule> AllModules() =>
        ModuleCatalog.DiscoverEnabled(Cfg(("Features:hello-world:Enabled", "true")));

    [Fact]
    public void All_on_default_enables_the_shipped_modules_but_not_the_disabled_example()
    {
        var ids = ModuleCatalog.DiscoverEnabled(Cfg()).Select(m => m.Id).ToHashSet();
        Assert.Contains("all-in-one", ids);
        foreach (var id in new[]
                 {
                     "terminal", "notifications", "scheduler", "logmonitor", "volumebackups", "webhooks",
                     "host-management", "deployment", "cve", "cloud-control", "image-updates", "agent",
                 })
            Assert.Contains(id, ids);
        Assert.DoesNotContain("hello-world", ids); // EnabledByDefault = false
    }

    [Fact]
    public void All_off_leaves_no_enabled_modules()
    {
        // Turn off every default-on module; the only default-off module (hello-world) stays off too.
        var offAll = ModuleCatalog.DiscoverEnabled(Cfg())
            .Select(m => ($"Features:{m.Id}:Enabled", (string?)"false")).ToArray();
        Assert.Empty(ModuleCatalog.DiscoverEnabled(Cfg(offAll)));
    }

    [Fact]
    public void Only_core_leaves_just_the_transitional_pseudo_module()
    {
        // Turn off every real feature module but keep the Core-carrying AllInOnePseudoModule.
        var off = ModuleCatalog.DiscoverEnabled(Cfg())
            .Where(m => m.Id != "all-in-one")
            .Select(m => ($"Features:{m.Id}:Enabled", (string?)"false")).ToArray();
        Assert.Equal(new[] { "all-in-one" },
            ModuleCatalog.DiscoverEnabled(Cfg(off)).Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Example_module_is_opt_in()
    {
        var ids = ModuleCatalog.DiscoverEnabled(Cfg(("Features:hello-world:Enabled", "true"))).Select(m => m.Id);
        Assert.Contains("hello-world", ids);
    }

    [Fact]
    public void Module_ids_and_nav_hrefs_are_unique_across_the_catalog()
    {
        var all = AllModules();
        var ids = all.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var hrefs = all.SelectMany(m => m.NavItems).Select(n => n.Href).ToList();
        Assert.Equal(hrefs.Count, hrefs.Distinct().Count());
    }

    [Fact]
    public void Every_module_ConfigureServices_runs_without_throwing()
    {
        foreach (var module in AllModules())
        {
            var services = new ServiceCollection();
            Assert.Null(Record.Exception(() => module.ConfigureServices(services, Cfg())));
        }
    }
}
