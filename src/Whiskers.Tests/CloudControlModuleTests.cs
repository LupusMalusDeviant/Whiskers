using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Mcp.Tools;
using Whiskers.Modules;
using Whiskers.Modules.CloudControl;
using Whiskers.Services.Cloud;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Hostinger;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 §3.6 — the CloudControl module move. A clean extraction: no Core service or page
/// consumes the cloud services (only the module's own page + the dedicated CloudTools/HetznerTools + the
/// module's CloudControlService), so no no-op defaults are needed and the /cloud page uses the ModuleGuard
/// wrapper pattern. (The §3.6 C10 ICloudProvider seam is deferred — a separate refactor of destructive
/// power/snapshot dispatch; this extraction is byte-identical.)</summary>
public class CloudControlModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_cloud_nav_and_the_cloud_and_hetzner_tools()
    {
        var module = new CloudControlModule();
        Assert.Equal("cloud-control", module.Id);
        var nav = Assert.Single(module.NavItems);
        Assert.Equal("cloud", nav.Href);
        Assert.Equal("Infrastruktur", nav.Group);
        Assert.Equal(new[] { typeof(CloudTools), typeof(HetznerTools) }, module.McpToolTypes);
    }

    [Fact]
    public void Cloud_nav_and_tools_moved_out_of_the_pseudo_module()
    {
        var pseudo = new AllInOnePseudoModule();
        Assert.DoesNotContain(pseudo.NavItems, n => n.Href == "cloud");
        Assert.DoesNotContain(typeof(CloudTools), pseudo.McpToolTypes);
        Assert.DoesNotContain(typeof(HetznerTools), pseudo.McpToolTypes);
    }

    // --- Registration ------------------------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_control_service_and_both_provider_clients()
    {
        var services = new ServiceCollection();
        new CloudControlModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(ICloudControlService) && d.ImplementationType == typeof(CloudControlService));
        // AddHttpClient<IHetznerService, HetznerApiService> / <IHostingerService, HostingerApiService> register the typed clients.
        Assert.Contains(services, d => d.ServiceType == typeof(IHetznerService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostingerService));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "cloud-control");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:cloud-control:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "cloud-control");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
