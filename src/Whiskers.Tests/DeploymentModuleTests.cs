using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Modules;
using Whiskers.Modules.Deployment;
using Whiskers.Services.Deployment;
using Whiskers.Services.ImageSearch;
using Whiskers.Services.Templates;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the Deployment/AppStore module move (the final Phase-1 extraction). Covers the
/// metadata (nav deploy + apps, no MCP tools of its own), the registrations (incl. the scoped IDeploymentService
/// and the three image-search providers), the ModuleCatalog gate, and the soft-dependency no-ops. The mixed,
/// Core-resident ContainerTools consumes IDeploymentService + ITemplateService and the AppStore page consumes
/// IImageSearchService, so all three get Core no-op defaults; the deploy no-op throws rather than fake a
/// deploy.</summary>
public class DeploymentModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_deploy_and_apps_nav_and_no_tools()
    {
        var module = new DeploymentModule();
        Assert.Equal("deployment", module.Id);
        Assert.Empty(module.McpToolTypes);
        Assert.Equal(new[] { "deploy", "apps" }, module.NavItems.Select(n => n.Href).ToArray());
        Assert.All(module.NavItems, n => Assert.Equal("Deployment", n.Group));
    }

    [Fact]
    public void Deploy_and_apps_nav_moved_out_but_compose_stays_in_the_pseudo_module()
    {
        var pseudo = new AllInOnePseudoModule().NavItems;
        Assert.DoesNotContain(pseudo, n => n.Href == "deploy");
        Assert.DoesNotContain(pseudo, n => n.Href == "apps");
        Assert.Contains(pseudo, n => n.Href == "compose"); // compose uses only Core services → stays Core
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_real_services_and_providers()
    {
        var services = new ServiceCollection();
        new DeploymentModule().ConfigureServices(services, Config());

        var dep = services.Last(d => d.ServiceType == typeof(IDeploymentService));
        Assert.Equal(typeof(DeploymentService), dep.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, dep.Lifetime); // matches the original AddScoped
        Assert.Contains(services, d => d.ServiceType == typeof(ITemplateService) && d.ImplementationType == typeof(TemplateService));
        Assert.Contains(services, d => d.ServiceType == typeof(IImageSearchService) && d.ImplementationType == typeof(ImageSearchService));
        Assert.Equal(3, services.Count(d => d.ServiceType == typeof(IImageSearchProvider)));
    }

    [Fact]
    public void Disabled_module_keeps_the_three_noops()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeploymentService, NoopDeploymentService>();
        services.AddSingleton<ITemplateService, NoopTemplateService>();
        services.AddSingleton<IImageSearchService, NoopImageSearchService>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType<NoopDeploymentService>(scope.ServiceProvider.GetRequiredService<IDeploymentService>());
        Assert.IsType<NoopTemplateService>(sp.GetRequiredService<ITemplateService>());
        Assert.IsType<NoopImageSearchService>(sp.GetRequiredService<IImageSearchService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noops_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeploymentService, NoopDeploymentService>();
        services.AddSingleton<ITemplateService, NoopTemplateService>();
        services.AddSingleton<IImageSearchService, NoopImageSearchService>();
        new DeploymentModule().ConfigureServices(services, Config());
        Assert.Equal(typeof(DeploymentService), services.Last(d => d.ServiceType == typeof(IDeploymentService)).ImplementationType);
        Assert.Equal(typeof(ImageSearchService), services.Last(d => d.ServiceType == typeof(IImageSearchService)).ImplementationType);
    }

    // --- No-op semantics (deploy throws; reads empty) ----------------------------------------------------

    [Fact]
    public async Task Noop_deploy_throws_and_validate_reports_invalid()
    {
        var noop = new NoopDeploymentService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.DeployFromFormAsync(new DeploymentRequest()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.DeployFromComposeAsync("services: {}"));
        Assert.False(noop.ValidateCompose("x").IsValid);
    }

    [Fact]
    public async Task Noop_reads_return_empty()
    {
        Assert.Empty(new NoopTemplateService().GetTemplates());
        Assert.Null(new NoopTemplateService().GetTemplate("nginx"));
        Assert.Empty(new NoopImageSearchService().GetRegistries());
        Assert.Empty(await new NoopImageSearchService().SearchAsync("nginx", null));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "deployment");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:deployment:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "deployment");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
