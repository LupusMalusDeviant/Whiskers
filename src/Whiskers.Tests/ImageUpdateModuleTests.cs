using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whiskers.Modules;
using Whiskers.Modules.ImageUpdate;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 §3.7 — the ImageUpdate/AutoUpdate module move (one module for both halves of the
/// feature). No nav and no MCP tools of its own (updates surface on the Dashboard; check_updates/update_container
/// live in the mixed, Core-resident ContainerTools). Covers the registrations (image-update checker + registry
/// client + store + the opt-in auto-updater), the ModuleCatalog gate, and the soft-dependency no-op: the store
/// is consumed by Core (ContainerTools + Dashboard), so a NoopImageUpdateStore default must resolve when the
/// module is off and be overridden when on.</summary>
public class ImageUpdateModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Has_no_nav_and_no_mcp_tools()
    {
        var module = new ImageUpdateModule();
        Assert.Equal("image-updates", module.Id);
        Assert.Empty(module.NavItems);
        Assert.Empty(module.McpToolTypes);
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_store_registry_and_both_hosted_services()
    {
        var services = new ServiceCollection();
        new ImageUpdateModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(IImageUpdateStore) && d.ImplementationType == typeof(ImageUpdateStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IRegistryClient));
        Assert.Contains(services, d => d.ServiceType == typeof(IAutoUpdateService));
        // ImageUpdateChecker + AutoUpdateService both run as hosted services.
        Assert.True(services.Count(d => d.ServiceType == typeof(IHostedService)) >= 2);
    }

    [Fact]
    public void Disabled_module_keeps_the_noop_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IImageUpdateStore, NoopImageUpdateStore>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopImageUpdateStore>(sp.GetRequiredService<IImageUpdateStore>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noop_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IImageUpdateStore, NoopImageUpdateStore>();
        new ImageUpdateModule().ConfigureServices(services, Config());
        Assert.Equal(typeof(ImageUpdateStore), services.Last(d => d.ServiceType == typeof(IImageUpdateStore)).ImplementationType);
    }

    [Fact]
    public void Noop_store_holds_no_updates()
    {
        var noop = new NoopImageUpdateStore();
        Assert.Empty(noop.GetAllPendingUpdates());
        Assert.Empty(noop.GetAll());
        Assert.Null(noop.Get("some-container"));
    }

    // --- C12 rollback no-op gate: since C12 the Dashboard consumes IAutoUpdateService too (rollback button +
    // capturing a snapshot before a manual update), so it needs a Core NoopAutoUpdateService default that
    // resolves when the module is off and is overridden by the real hosted service when on. ------------------

    [Fact]
    public void Disabled_module_keeps_the_noop_auto_update_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAutoUpdateService, NoopAutoUpdateService>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopAutoUpdateService>(sp.GetRequiredService<IAutoUpdateService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noop_auto_update_service_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAutoUpdateService, NoopAutoUpdateService>();
        new ImageUpdateModule().ConfigureServices(services, Config());
        // The module forwards IAutoUpdateService to the shared hosted AutoUpdateService via a factory, so the
        // last registration is factory-based (no ImplementationType) — i.e. it is NOT the no-op type default.
        var last = services.Last(d => d.ServiceType == typeof(IAutoUpdateService));
        Assert.Null(last.ImplementationType);
        Assert.NotNull(last.ImplementationFactory);
    }

    [Fact]
    public async Task Noop_auto_update_service_has_nothing_and_refuses_rollback()
    {
        var noop = new NoopAutoUpdateService();
        Assert.Empty(await noop.GetRollbacksAsync());
        Assert.Empty(await noop.GetPoliciesAsync());
        Assert.Empty(await noop.GetHistoryAsync());
        // With the module off there is no snapshot to roll back to — the call is defensively refused (and the
        // UI never offers it, since GetRollbacksAsync is empty).
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.RollbackAsync(1));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "image-updates");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:image-updates:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "image-updates");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
