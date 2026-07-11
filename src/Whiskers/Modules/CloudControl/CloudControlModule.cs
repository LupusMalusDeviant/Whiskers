using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Cloud;
using Whiskers.Services.Cloud.Providers;
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
/// <b>C10 done (§3.6):</b> Hetzner/Hostinger are now pluggable <see cref="Providers.ICloudProvider"/>
/// implementations (multi-registration, selected by <c>ServerConfig.CloudProvider</c>) behind
/// <c>CloudControlService</c>, with <see cref="Providers.IHetznerExtensions"/> for the Hetzner-only tools and
/// CancellationTokens threaded through the clients (OPT-12). The enum stays the persisted key, so servers.json
/// is unchanged; see <c>Services/Cloud/Providers/</c>.</summary>
public sealed class CloudControlModule : IWhiskersModule
{
    public string Id => "cloud-control";
    public string DisplayName => "Cloud";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("cloud", "Nav_Cloud", Icons.Material.Filled.CloudQueue, "Infrastruktur", AppRole.Viewer, 220),
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

        // C10: provider seam. The API clients above stay as the HTTP layer; each provider adapts one to
        // ICloudProvider (multi-registration → CloudControlService dispatches by ServerConfig.CloudProvider,
        // no hard enum-switch). The Hetzner provider also serves IHetznerExtensions (same instance) for the
        // Hetzner-only MCP tools (rescue, backups, snapshot management).
        services.AddSingleton<HetznerCloudProvider>();
        services.AddSingleton<ICloudProvider>(sp => sp.GetRequiredService<HetznerCloudProvider>());
        services.AddSingleton<IHetznerExtensions>(sp => sp.GetRequiredService<HetznerCloudProvider>());
        services.AddSingleton<ICloudProvider, HostingerCloudProvider>();
        services.AddSingleton<ICloudControlService, CloudControlService>();
    }
}
