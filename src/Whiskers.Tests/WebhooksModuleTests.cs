using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Modules;
using Whiskers.Modules.Webhooks;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the Webhooks module move. Covers the module metadata (nav "webhooks", no MCP
/// tools), the registration, the ModuleCatalog gate, and the soft-dependency no-op: the inbound
/// /api/webhooks/{id} endpoint stays in Core and resolves IWebhookService per request, so a NoopWebhookService
/// default must resolve when the module is off and be overridden by the real service when on. The no-op's
/// TriggerAsync fails gracefully (so the endpoint answers 400, not 500) and CreateWebhookAsync throws.</summary>
public class WebhooksModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_webhooks_nav_entry_and_no_tools()
    {
        var module = new WebhooksModule();
        var nav = Assert.Single(module.NavItems);
        Assert.Equal("webhooks", nav.Href);
        Assert.Equal("Automatisierung", nav.Group);
        Assert.Equal(320, nav.Order);
        Assert.Empty(module.McpToolTypes);
    }

    [Fact]
    public void Webhooks_nav_moved_out_of_the_pseudo_module()
    {
        Assert.DoesNotContain(new AllInOnePseudoModule().NavItems, n => n.Href == "webhooks");
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_real_webhook_service()
    {
        var services = new ServiceCollection();
        new WebhooksModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(IWebhookService) && d.ImplementationType == typeof(WebhookService));
    }

    [Fact]
    public void Disabled_module_keeps_the_noop_webhook_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookService, NoopWebhookService>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopWebhookService>(sp.GetRequiredService<IWebhookService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noop_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookService, NoopWebhookService>();
        new WebhooksModule().ConfigureServices(services, Config());
        var last = services.Last(d => d.ServiceType == typeof(IWebhookService));
        Assert.Equal(typeof(WebhookService), last.ImplementationType);
    }

    // --- No-op semantics (endpoint stays graceful; page mutations throw) ---------------------------------

    [Fact]
    public async Task Noop_trigger_fails_gracefully_so_the_endpoint_answers_400_not_500()
    {
        var (success, output) = await new NoopWebhookService().TriggerAsync("some-id", body: "{}");
        Assert.False(success);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task Noop_reads_empty_and_create_throws()
    {
        var noop = new NoopWebhookService();
        Assert.Empty(await noop.GetWebhooksAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.CreateWebhookAsync(new WebhookEntity { Name = "x" }));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "webhooks");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:webhooks:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "webhooks");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
