using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Models;
using Whiskers.Services.GitDeploy;

namespace Whiskers.Modules.GitDeploy;

/// <summary>Git-based deployments (missingFeatures F5): the `/git-deploy` page and the
/// <see cref="IGitDeployService"/> that clones/builds/ups a repo on the target server. The Webhooks
/// module triggers redeploys through the Core contract ("git-deploy" action); Core registers a
/// <see cref="NoopGitDeployService"/> default so that path answers gracefully when this module is
/// off (soft-dependency pattern, RoadToSAP §2.1). No MCP tools in v1.</summary>
public sealed class GitDeployModule : IWhiskersModule
{
    public string Id => "gitdeploy";
    public string DisplayName => "Git Deploy";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("git-deploy", "Nav_GitDeploy", Icons.Material.Filled.CloudSync, "Deployment", AppRole.Viewer, 115),
    };

    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Wins over Core's NoopGitDeployService by last-registration.
        services.AddSingleton<IGitDeployService, GitDeployService>();
    }
}
