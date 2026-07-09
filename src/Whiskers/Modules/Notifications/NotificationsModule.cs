using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Services.Notifications;

namespace Whiskers.Modules.Notifications;

/// <summary>The outbound notification channels (Mattermost, Matrix, Telegram, ntfy, Discord, Slack, Email,
/// generic webhook) plus the <see cref="CompositeNotificationService"/> that fans an event out over them
/// (RoadToSAP Phase 1). Registrations are moved <b>verbatim</b> from Program.cs — this is a relocation.
///
/// Deliberately kept in <b>Core</b>, NOT in this module: the in-app feed store
/// (<c>IInAppNotificationStore</c>) and the per-container prefs (<c>IContainerNotificationPrefsService</c>).
/// Those are notification <i>data</i> (they keep the bell + /notifications page working when the module is
/// off) and the prefs service is an <c>IInitializable</c> in the Core startup loop. When this module is
/// disabled the Core's <see cref="NoopNotificationService"/> stays registered, so every
/// <c>INotificationService</c> consumer (CVE, Health, ImageUpdate, AutoUpdate, Metrics, LogMonitor,
/// AI triggers, approvals) still resolves — no channels are wired, and nothing breaks.
///
/// No sidebar entry (the "Benachrichtigungen" feed nav item stays in Core) and no MCP tools; the Settings
/// panels are hidden via <c>IModuleRegistry.IsEnabled("notifications")</c>.</summary>
public sealed class NotificationsModule : IWhiskersModule
{
    public string Id => "notifications";
    public string DisplayName => "Benachrichtigungen";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public IReadOnlyList<NavItem> NavItems => Array.Empty<NavItem>();
    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite).

        // Per-channel typed settings (were scattered in the Program.cs "Configuration" block).
        services.Configure<MattermostSettings>(config.GetSection(MattermostSettings.SectionName));
        services.Configure<Whiskers.Configuration.MatrixSettings>(config.GetSection(Whiskers.Configuration.MatrixSettings.SectionName));
        services.Configure<TelegramSettings>(config.GetSection(TelegramSettings.SectionName));
        services.Configure<NtfySettings>(config.GetSection(NtfySettings.SectionName));
        services.Configure<DiscordSettings>(config.GetSection(DiscordSettings.SectionName));
        services.Configure<SlackSettings>(config.GetSection(SlackSettings.SectionName));
        services.Configure<EmailSettings>(config.GetSection(EmailSettings.SectionName));
        services.Configure<WebhookNotificationSettings>(config.GetSection(WebhookNotificationSettings.SectionName));

        // Notifications (Mattermost + Matrix via composite)
        services.AddHttpClient<MattermostNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<MattermostNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.IMattermostNotificationService>(sp => sp.GetRequiredService<MattermostNotificationService>());
        services.AddHttpClient<MatrixNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<MatrixNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.IMatrixNotificationService>(sp => sp.GetRequiredService<MatrixNotificationService>());
        // Additional channels (Telegram, ntfy, Discord, Email, generic webhook) — same composite fan-out.
        services.AddHttpClient<Whiskers.Services.Notifications.TelegramNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Whiskers.Services.Notifications.TelegramNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.ITelegramNotificationService>(sp => sp.GetRequiredService<Whiskers.Services.Notifications.TelegramNotificationService>());
        services.AddHttpClient<Whiskers.Services.Notifications.NtfyNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Whiskers.Services.Notifications.NtfyNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.INtfyNotificationService>(sp => sp.GetRequiredService<Whiskers.Services.Notifications.NtfyNotificationService>());
        services.AddHttpClient<Whiskers.Services.Notifications.DiscordNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Whiskers.Services.Notifications.DiscordNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.IDiscordNotificationService>(sp => sp.GetRequiredService<Whiskers.Services.Notifications.DiscordNotificationService>());
        services.AddHttpClient<Whiskers.Services.Notifications.SlackNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Whiskers.Services.Notifications.SlackNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.ISlackNotificationService>(sp => sp.GetRequiredService<Whiskers.Services.Notifications.SlackNotificationService>());
        services.AddSingleton<Whiskers.Services.Notifications.IEmailNotificationService, Whiskers.Services.Notifications.EmailNotificationService>();
        services.AddHttpClient<Whiskers.Services.Notifications.WebhookNotificationService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Whiskers.Services.Notifications.WebhookNotificationService>();
        services.AddSingleton<Whiskers.Services.Notifications.IWebhookNotificationService>(sp => sp.GetRequiredService<Whiskers.Services.Notifications.WebhookNotificationService>());
        // Expose each channel as INotificationChannel so CompositeNotificationService fans out over the set
        // (changeme C9). Registration order = the test-report order; keep it stable (Mattermost … Webhook).
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<IMattermostNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<IMatrixNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<ITelegramNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<INtfyNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<IDiscordNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<ISlackNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<IEmailNotificationService>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<IWebhookNotificationService>());
        // The composite fans out over the channels + the Core in-app feed store. Registered here (after the
        // Core NoopNotificationService, which the module loop runs before) so it wins by last-registration
        // when the module is enabled; when it's disabled the Noop remains. (RoadToSAP §2.1 soft dependency.)
        services.AddSingleton<INotificationService, CompositeNotificationService>();
    }
}
