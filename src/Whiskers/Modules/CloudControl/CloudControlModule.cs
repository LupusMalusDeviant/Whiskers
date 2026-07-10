using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Cloud;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Hostinger;

namespace Whiskers.Modules.CloudControl;

/// <summary>Out-of-band cloud control (RoadToSAP Phase 1, §3 item 6): power actions + snapshots on cloud
/// servers via provider APIs (Hetzner, Hostinger), the `/cloud` page and the cloud/Hetzner MCP tools.
/// Registrations are moved <b>verbatim</b> from Program.cs.
///
/// Clean extraction — no Core service or page consumes these services (only the module's own page +
/// <c>CloudTools</c>/<c>HetznerTools</c>, both dedicated, + <c>CloudControlService</c> itself), so no no-op
/// defaults are needed; the `/cloud` page uses the thin-wrapper + <c>ModuleGuard</c> pattern instead.
///
/// <b>Deferred (§3.6 assigns C10):</b> the <c>ICloudProvider</c> seam (making Hetzner/Hostinger pluggable
/// providers behind a common contract, like <c>IVpnProvider</c>) is a separate refactor of destructive
/// power/snapshot dispatch — not bundled here; this PR keeps the extraction byte-identical.</summary>
public sealed class CloudControlModule : IWhiskersModule
{
    public string Id => "cloud-control";
    public string DisplayName => "Cloud";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("cloud", "Cloud", Icons.Material.Filled.CloudQueue, "Infrastruktur", AppRole.Viewer, 220),
    };

    public IReadOnlyList<Type> McpToolTypes { get; } = new[] { typeof(CloudTools), typeof(HetznerTools) };

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation). Rotating primary handler (PooledConnectionLifetime) so
        // these long-lived cloud clients re-resolve DNS periodically instead of pinning a stale IP.
        services.AddHttpClient<IHetznerService, HetznerApiService>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddHttpClient<IHostingerService, HostingerApiService>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton<ICloudControlService, CloudControlService>();
    }
}
