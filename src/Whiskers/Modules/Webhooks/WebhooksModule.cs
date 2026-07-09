using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Models;
using Whiskers.Services.Webhooks;

namespace Whiskers.Modules.Webhooks;

/// <summary>Inbound CI/CD webhooks (RoadToSAP Phase 1): the `/webhooks` management page and the
/// <c>IWebhookService</c> that processes incoming triggers (restart/rebuild/deploy). Registration is moved
/// <b>verbatim</b> from Program.cs. No MCP tools.
///
/// The inbound endpoint <c>POST /api/webhooks/{id}</c> stays in Program.cs (an app-level route can't move into
/// <c>ConfigureServices</c>) and resolves <c>IWebhookService</c> per request. So Core registers a
/// <see cref="NoopWebhookService"/> default before the module loop; the real <see cref="WebhookService"/> wins
/// by last-registration when enabled, and the no-op makes a trigger against a disabled module answer 400 (not
/// 500). The <c>/webhooks</c> page is gated by <c>ModuleGuard</c>.</summary>
public sealed class WebhooksModule : IWhiskersModule
{
    public string Id => "webhooks";
    public string DisplayName => "Webhooks";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // The "webhooks" sidebar entry (moved verbatim from AllInOnePseudoModule); shown only while enabled.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("webhooks", "Webhooks", Icons.Material.Filled.Webhook, "Automatisierung", AppRole.Viewer, 320),
    };

    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite). Registered after Core's
        // NoopWebhookService (the module loop runs after it), so the real service wins here.
        services.AddSingleton<IWebhookService, WebhookService>();
    }
}
