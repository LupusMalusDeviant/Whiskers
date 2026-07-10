using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Modules.ImageUpdate;

/// <summary>Image-update checking + opt-in auto-update (RoadToSAP Phase 1, §3 item 7). One module for both
/// (they're two halves of the same feature): the background <c>ImageUpdateChecker</c> that polls registries
/// for newer image digests into <c>IImageUpdateStore</c>, its registry client, and the opt-in
/// <c>AutoUpdateService</c> that applies them. Registrations are moved <b>verbatim</b> from Program.cs.
///
/// No nav entry and no MCP tools of its own: updates surface on the Dashboard, and the
/// <c>check_updates</c>/<c>update_container</c> MCP tools live in the mixed, Core-resident <c>ContainerTools</c>
/// (which also holds list/restart/logs/…), so they can't move. Because <c>ContainerTools</c> and the Dashboard
/// page consume <c>IImageUpdateStore</c>, Core registers a <see cref="NoopImageUpdateStore"/> default before
/// the module loop; the real store wins by last-registration when enabled. With the module off the checker +
/// auto-updater simply don't run and the Dashboard shows no pending updates.</summary>
public sealed class ImageUpdateModule : IWhiskersModule
{
    public string Id => "image-updates";
    public string DisplayName => "Image-Updates";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public IReadOnlyList<NavItem> NavItems => Array.Empty<NavItem>();
    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Image update checking (moved verbatim from Program.cs). Registry client: a typed HttpClient with a
        // rotating primary handler so a long-lived resolution doesn't pin a stale DNS answer; IRegistryClient
        // stays the shared-cache singleton, forwarded to the typed client.
        services.Configure<ImageUpdateSettings>(config.GetSection("ImageUpdate"));
        services.AddHttpClient<RegistryClient>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton<IImageUpdateStore, ImageUpdateStore>();
        services.AddSingleton<IRegistryClient>(sp => sp.GetRequiredService<RegistryClient>());
        services.AddHostedService<ImageUpdateChecker>();

        // Auto-update (opt-in only) — moved verbatim.
        services.AddSingletonWithInterfaceAndHostedService<AutoUpdateService, IAutoUpdateService>();
    }
}
