using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
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

    /// <summary>Upgrade path for HOCH-12 part 2 (webhook secrets are mandatory now): legacy webhooks
    /// created before this release may have an empty secret, i.e. an unauthenticated remote trigger.
    /// They are DISABLED (never deleted — the config survives so the owner can regenerate a secret and
    /// re-enable) and the admin is notified once. Runs after the DB migration (see RunWhiskersStartupAsync).</summary>
    public async Task InitializeAsync(IServiceProvider sp, CancellationToken ct)
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var legacy = await db.Webhooks
            .Where(w => w.Enabled && (w.Secret == null || w.Secret == ""))
            .ToListAsync(ct);
        if (legacy.Count == 0) return;

        foreach (var webhook in legacy) webhook.Enabled = false;
        await db.SaveChangesAsync(ct);

        var names = string.Join(", ", legacy.Select(w => w.Name));
        sp.GetService<ILogger<WebhooksModule>>()?.LogWarning(
            "Disabled {Count} legacy webhook(s) without a secret (secrets are mandatory now): {Names}. " +
            "Regenerate their secrets in the Webhooks UI to re-enable them.", legacy.Count, names);

        var notify = sp.GetService<INotificationService>();
        if (notify is null) return;
        try
        {
            await notify.SendAsync(new NotificationEvent
            {
                EventType = "webhook_disabled",
                ImageInfo = $"{legacy.Count} Webhook(s) ohne Secret wurden deaktiviert (Secret ist jetzt Pflicht): {names}. " +
                            "Secret in der Webhook-Ansicht neu generieren und wieder aktivieren.",
            });
        }
        catch
        {
            // Notification is best-effort — the migration itself already succeeded and was logged.
        }
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite). Registered after Core's
        // NoopWebhookService (the module loop runs after it), so the real service wins here.
        services.AddSingleton<IWebhookService, WebhookService>();
    }
}
