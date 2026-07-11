using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Models;
using Whiskers.Services.Deployment;
using Whiskers.Services.ImageSearch;
using Whiskers.Services.ImageSearch.Providers;
using Whiskers.Services.Templates;

namespace Whiskers.Modules.Deployment;

/// <summary>App deployment + the app store (RoadToSAP Phase 1, the final Phase-1 extraction): the `/deploy`
/// page (form + compose deploy via <c>IDeploymentService</c>), the `/apps` page (built-in templates via
/// <c>ITemplateService</c> and multi-registry image search via <c>IImageSearchService</c> + its providers).
/// Registrations are moved <b>verbatim</b> from Program.cs. The `/compose` editor stays in Core (it uses only
/// <c>IDockerService</c>/<c>IHostCommandExecutor</c>), so its nav entry remains in the pseudo-module.
///
/// No MCP tools of its own: <c>deploy_app</c>/<c>deploy_compose</c> live in the mixed, Core-resident
/// <c>ContainerTools</c> (which also holds list/restart/logs/…), so it can't be split. Core therefore keeps
/// no-op defaults (<see cref="NoopDeploymentService"/>, <c>NoopTemplateService</c>, <c>NoopImageSearchService</c>)
/// for when this module is off; the real services registered here win by last-registration when enabled.</summary>
public sealed class DeploymentModule : IWhiskersModule
{
    public string Id => "deployment";
    public string DisplayName => "Bereitstellung";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // "deploy" + "apps" (moved verbatim from AllInOnePseudoModule). "compose" stays in Core.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("deploy", "Nav_Deploy", Icons.Material.Filled.RocketLaunch, "Deployment", AppRole.Viewer, 110),
        new NavItem("apps",   "Nav_AppStore",     Icons.Material.Filled.Apps,         "Deployment", AppRole.Viewer, 130),
    };

    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs. Registered after Core's Noop* defaults (the module loop runs after
        // them), so the real services win here by last-registration.
        services.AddScoped<IDeploymentService, DeploymentService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.Configure<ImageSearchSettings>(config.GetSection(ImageSearchSettings.SectionName));
        services.AddSingleton<IImageSearchProvider, DockerHubSearchProvider>();
        services.AddSingleton<IImageSearchProvider, GhcrSearchProvider>();
        services.AddSingleton<IImageSearchProvider, HarborSearchProvider>();
        services.AddSingleton<IImageSearchService, ImageSearchService>();
    }
}
