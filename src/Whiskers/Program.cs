using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Whiskers.Components;
using Whiskers.Configuration;
using Whiskers.Utils;
using Whiskers.Hubs;
using Whiskers.Mcp;
using Whiskers.Mcp.Tools;
using Whiskers.Services.Deployment;
using Whiskers.Services.Docker;
using Whiskers.Services.HealthMonitor;
using Whiskers.Services.Metrics;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Services.Auth;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Cve;
using Whiskers.Services.Mcp;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Hostinger;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Central data-directory resolver (WHISKERS_DATA_DIR, default /app/data). Built here at bootstrap
// because the config layers, DataProtection keys and the DbContext connection string below all need
// it before the DI container exists. The same instance is registered as a singleton (further down)
// so every service resolves paths through it instead of hard-coding /app/data.
var dataPaths = DataPathOptions.FromConfiguration(builder.Configuration);

// One-time data migration CLI: `dotnet Whiskers.dll --migrate-to-postgres "<npgsql-conn>"`. Copies the
// SQLite data into a fresh PostgreSQL database and exits WITHOUT booting the web host. The source is never
// modified; the target must be empty (see SqliteToPostgresMigrator). Guarded so a normal boot is untouched.
if (args is ["--migrate-to-postgres", ..])
{
    var targetConn = args.Length > 1 ? args[1] : "";
    return await SqliteToPostgresMigrator.RunAsync(dataPaths, targetConn, Console.Out);
}

// UI-writable agent provider settings (overrides only Agent:* keys; reloadOnChange → IOptionsMonitor
// picks up UI changes without a restart). As the last source, so the UI takes precedence over env/appsettings.
builder.Configuration.AddJsonFile(dataPaths.AgentSettingsJson, optional: true, reloadOnChange: true);
// UI-writable settings for all other sections (Mattermost, Matrix, HealthMonitor, CveMonitor,
// ImageUpdate, MetricAlert, …). Last layer → overrides env; reloadOnChange → applied live.
builder.Configuration.AddJsonFile(dataPaths.AppSettingsJson, optional: true, reloadOnChange: true);

// Path base for reverse proxy subpath
var configuredPathBase = builder.Configuration["PathBase"] ?? "";

// Register the data-path resolver so every consumer can inject it (DI fills the optional
// DataPathOptions constructor parameter from this instance).
builder.Services.AddSingleton(dataPaths);

// Persist data protection keys so antiforgery tokens survive container restarts
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataPaths.KeysDir))
    .SetApplicationName("ServerWatch");

// Core default so every INotificationService consumer (CVE, Health, ImageUpdate, AutoUpdate, Metrics,
// LogMonitor, AI triggers, approvals) resolves even when the Notifications module is off. That module
// registers its CompositeNotificationService inside the loop below, which then wins by last-registration;
// with the module off this Noop stays. MUST be registered BEFORE the module loop. (RoadToSAP §2.1)
builder.Services.AddSingleton<INotificationService, NoopNotificationService>();
// Same soft-dependency pattern for log-alert rules: the Core AI-triggers page reads/creates rules via
// ILogMonitorService, so it needs a default when the LogMonitor module is off. The module registers the real
// hosted LogMonitorService in the loop below, which then wins by last-registration. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.LogMonitor.ILogMonitorService, Whiskers.Services.LogMonitor.NoopLogMonitorService>();
// Same pattern for volume backups: the Scheduler module's TaskExecutor injects IVolumeBackupService (for
// VolumeBackup tasks), so it needs a default when the VolumeBackups module is off — otherwise ValidateOnBuild
// can't construct TaskExecutor. The module registers the real service in the loop below and wins. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.Backup.IVolumeBackupService, Whiskers.Services.Backup.NoopVolumeBackupService>();
// And for webhooks: the inbound /api/webhooks/{id} endpoint (below, in Core) resolves IWebhookService per
// request, so it needs a default when the Webhooks module is off. The module registers the real service in
// the loop below and wins; with it off, a trigger answers 400 instead of 500. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.Webhooks.IWebhookService, Whiskers.Services.Webhooks.NoopWebhookService>();
// And for host management: ServerTools (an MCP class mixing core server ops with firewall/nginx/systemd/ssl
// ops) can't be split under the byte-gleich rule, so it stays in Core and injects these four services per
// call. Their no-op defaults keep that working when the HostManagement module is off; the module registers the
// real services in the loop below and wins. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.Server.IFirewallService, Whiskers.Services.Server.NoopFirewallService>();
builder.Services.AddSingleton<Whiskers.Services.Server.INginxService, Whiskers.Services.Server.NoopNginxService>();
builder.Services.AddSingleton<Whiskers.Services.Server.ISystemdService, Whiskers.Services.Server.NoopSystemdService>();
builder.Services.AddSingleton<Whiskers.Services.Server.ISslCertService, Whiskers.Services.Server.NoopSslCertService>();
// And for deployment/app-store: ContainerTools (mixed: core container ops + deploy_app/deploy_compose) stays
// in Core and injects IDeploymentService/ITemplateService per call, and the AppStore page injects
// IImageSearchService, so all three need defaults when the Deployment module is off. The module registers the
// real services in the loop below and wins. IDeploymentService is scoped, matching the real one. (RoadToSAP §2.1)
builder.Services.AddScoped<Whiskers.Services.Deployment.IDeploymentService, Whiskers.Services.Deployment.NoopDeploymentService>();
builder.Services.AddSingleton<Whiskers.Services.Templates.ITemplateService, Whiskers.Services.Templates.NoopTemplateService>();
builder.Services.AddSingleton<Whiskers.Services.ImageSearch.IImageSearchService, Whiskers.Services.ImageSearch.NoopImageSearchService>();
// And for CVE: the findings store + monitor are consumed by Core pages (Dashboard, ContainerDetail, Settings),
// so they need defaults when the Cve module is off; the age-store no-op keeps the inline-gated /cves page's
// injection safe. The module registers the real services in the loop below and wins. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.Cve.ICveFindingsStore, Whiskers.Services.Cve.NoopCveFindingsStore>();
builder.Services.AddSingleton<Whiskers.Services.Cve.ICveMonitorService, Whiskers.Services.Cve.NoopCveMonitorService>();
builder.Services.AddSingleton<Whiskers.Services.Cve.ICveAgeStore, Whiskers.Services.Cve.NoopCveAgeStore>();
// And for image updates: ContainerTools (mixed: core container ops + check_updates/update_container) and the
// Dashboard page consume IImageUpdateStore, so it needs a default when the ImageUpdate module is off. The
// module registers the real store in the loop below and wins. (RoadToSAP §2.1)
builder.Services.AddSingleton<Whiskers.Services.ImageUpdate.IImageUpdateStore, Whiskers.Services.ImageUpdate.NoopImageUpdateStore>();

// Module pipeline (RoadToSAP Phase 1). Discover enabled modules early (Features:<id>:Enabled overrides each
// module's default) so their services, MCP tools and navigation all come from one list; the MCP-tool and
// nav registrations further down read `modules`. Each enabled module's ConfigureServices moves its former
// inline Program.cs registrations here verbatim (Terminal, Notifications, … extracted one PR at a time).
var modules = Whiskers.Modules.ModuleCatalog.DiscoverEnabled(builder.Configuration);
foreach (var module in modules)
    module.ConfigureServices(builder.Services, builder.Configuration);

// Configuration
builder.Services.Configure<DockerSettings>(builder.Configuration.GetSection(DockerSettings.SectionName));
builder.Services.Configure<GoogleAuthSettings>(builder.Configuration.GetSection(GoogleAuthSettings.SectionName));
builder.Services.Configure<HealthMonitorSettings>(builder.Configuration.GetSection(HealthMonitorSettings.SectionName));
// The 8 notification-channel settings (Mattermost, Matrix, Telegram, ntfy, Discord, Slack, Email, Webhook)
// moved into Modules/Notifications alongside their channel registrations (RoadToSAP Phase 1).

// Server config + Docker services
builder.Services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService, ServerConfigService>();
builder.Services.AddSingleton<Whiskers.Services.Docker.ISshTunnelManager, SshTunnelManager>();
builder.Services.AddSingleton<Whiskers.Services.Docker.IDockerConnectionManager, DockerConnectionManager>();
builder.Services.AddSingleton<IDockerService, DockerService>();

// Health monitoring
builder.Services.AddSingleton<IHealthStore, InMemoryHealthStore>();
builder.Services.AddHostedService<ContainerHealthMonitor>();

// In-app notification feed (bell + /notifications page). Stays in Core — NOT the Notifications module —
// because it's the notification DATA store: the composite writes to it and the feed page/bell read it even
// when the module is off. The 8 outbound channels + CompositeNotificationService live in
// Modules/Notifications; when that module is enabled its composite fans out over the channels and this feed.
builder.Services.AddSingleton<Whiskers.Services.Notifications.IInAppNotificationStore, Whiskers.Services.Notifications.InAppNotificationStore>();

// Keep capability-bearing notification URLs (Telegram bot token in the path, Discord/Slack/Mattermost/
// ntfy/webhook secret URLs) out of the HttpClient request log — the default HttpClient logger writes the
// full request URI at Information level. Raise these categories to Warning so the URL isn't logged.
foreach (var httpClientName in new[]
         {
             "TelegramNotificationService", "MattermostNotificationService", "DiscordNotificationService",
             "SlackNotificationService", "NtfyNotificationService", "WebhookNotificationService"
         })
    builder.Logging.AddFilter($"System.Net.Http.HttpClient.{httpClientName}", Microsoft.Extensions.Logging.LogLevel.Warning);

// In-app user handbook (Hilfe page)
builder.Services.AddSingleton<Whiskers.Services.Help.IHelpContentService, Whiskers.Services.Help.HelpContentService>();

// Deployment (IDeploymentService) moved to Modules/Deployment (RoadToSAP Phase 1). Core keeps a
// NoopDeploymentService default (registered above) for ContainerTools + the /deploy page when it's off.

// Image-update checking + auto-update moved to Modules/ImageUpdate (RoadToSAP Phase 1 §3.7). Core keeps a
// NoopImageUpdateStore default (registered above) for ContainerTools + the Dashboard page when it's off.

// CVE monitoring (Trivy + apt) moved to Modules/Cve (RoadToSAP Phase 1 §3.5). Core keeps Noop CVE defaults
// (registered above) for the Dashboard/ContainerDetail/Settings pages when the module is off.

// Auth whitelist + roles
builder.Services.AddSingleton<Whiskers.Services.Auth.IWhitelistService, WhitelistService>();
builder.Services.AddSingleton<Whiskers.Services.Auth.IRoleService, Whiskers.Services.Auth.RoleService>();
// Per-circuit current-user/role resolver (scoped — depends on the scoped AuthenticationStateProvider)
builder.Services.AddScoped<Whiskers.Services.Auth.ICurrentUserService, Whiskers.Services.Auth.CurrentUserService>();

// Notification prefs per container
builder.Services.AddSingleton<Whiskers.Services.Notifications.IContainerNotificationPrefsService, Whiskers.Services.Notifications.ContainerNotificationPrefsService>();

// Secret vault
builder.Services.AddSingleton<Whiskers.Services.Vault.IVaultService, Whiskers.Services.Vault.VaultService>();

// Cloud provider integrations (Hetzner/Hostinger + ICloudControlService) moved to Modules/CloudControl
// (RoadToSAP Phase 1 §3.6). Clean extraction — no Core consumer, so no no-op defaults are needed.

// Host command execution + server management. IHostCommandExecutor stays in Core (shared by many services).
// The firewall/nginx/systemd/ssl services moved to Modules/HostManagement; Core keeps their Noop* defaults
// (registered above) for ServerTools + the pages when that module is off. (RoadToSAP Phase 1)
builder.Services.AddSingleton<IHostCommandExecutor, HostCommandExecutor>();
builder.Services.AddSingleton<Whiskers.Services.Onboarding.IOnboardingService, Whiskers.Services.Onboarding.OnboardingService>();

// Metrics database — SQLite (zero-config default) or PostgreSQL, selected by Database:Provider
// (WHISKERS_DB_PROVIDER / _CONNECTION[_FILE]). SQLite default is byte-identical to before. (stableDB.md)
builder.AddWhiskersDatabase(dataPaths);

// Liveness/readiness health checks, surfaced at /healthz and /readyz below (Docker HEALTHCHECK +
// K8s probes). Only readiness checks carry the "ready" tag; /healthz stays dependency-free.
builder.Services.AddHealthChecks()
    .AddCheck<DbReadyCheck>("db", tags: new[] { "ready" })
    .AddCheck<ServerConfigReadyCheck>("serverconfig", tags: new[] { "ready" });
builder.Services.Configure<MetricsSettings>(builder.Configuration.GetSection(MetricsSettings.SectionName));
builder.Services.Configure<MetricAlertSettings>(builder.Configuration.GetSection(MetricAlertSettings.SectionName));
builder.Services.AddSingleton<Whiskers.Services.Persistence.IAppSettingsStore, Whiskers.Services.Persistence.AppSettingsStore>();
builder.Services.AddSingleton<Whiskers.Services.Metrics.IMetricsQueryService, MetricsQueryService>();
// Metrics source seam: collector reads through IMetricsSource so a server can be switched to a
// push/scrape TSDB (VictoriaMetrics) instead of SSH/Docker. Docker is the default + fallback.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Whiskers.Services.Metrics.IDockerMetricsSource, DockerMetricsSource>();
builder.Services.AddSingleton<Whiskers.Services.Metrics.IPrometheusMetricsSource, PrometheusMetricsSource>();
builder.Services.AddSingleton<IMetricsSource, MetricsSourceDispatcher>();
builder.Services.AddHostedService<MetricsCollectorService>();

// Database service
builder.Services.AddSingleton<Whiskers.Services.Database.IDatabaseService, Whiskers.Services.Database.DatabaseService>();

// Scheduler (ITaskExecutor + SchedulerService hosted) moved to Modules/Scheduler (RoadToSAP Phase 1).

// App templates + multi-registry image search ("marketplaces") moved to Modules/Deployment (RoadToSAP
// Phase 1). Core keeps NoopTemplateService + NoopImageSearchService defaults (registered above) for
// ContainerTools + the AppStore page when the module is off.

// Mesh VPN provider abstraction (decoupled from the app image). Default provider "none" = VPN on
// host/sidecar (or legacy entrypoint.sh); "tailscale"/"netbird" let the app manage it.
builder.Services.Configure<Whiskers.Services.Vpn.VpnSettings>(
    builder.Configuration.GetSection(Whiskers.Services.Vpn.VpnSettings.SectionName));
// Derive the mesh-VPN state paths from the central data directory when the operator hasn't set them
// explicitly (keeps them in sync with WHISKERS_DATA_DIR instead of a second hard-coded /app/data).
builder.Services.PostConfigure<Whiskers.Services.Vpn.VpnSettings>(s =>
{
    if (string.IsNullOrWhiteSpace(s.Tailscale.StateDir)) s.Tailscale.StateDir = dataPaths.TailscaleStateDir;
    if (string.IsNullOrWhiteSpace(s.Netbird.ConfigPath)) s.Netbird.ConfigPath = dataPaths.NetbirdConfigPath;
});
builder.Services.AddSingleton<Whiskers.Services.Vpn.IVpnProvider, Whiskers.Services.Vpn.Providers.TailscaleVpnProvider>();
builder.Services.AddSingleton<Whiskers.Services.Vpn.IVpnProvider, Whiskers.Services.Vpn.Providers.NetbirdVpnProvider>();
builder.Services.AddSingleton<Whiskers.Services.Vpn.IVpnProvider, Whiskers.Services.Vpn.Providers.NoopVpnProvider>();
builder.Services.AddSingleton<Whiskers.Services.Vpn.IVpnService, Whiskers.Services.Vpn.VpnService>();
builder.Services.AddHostedService<Whiskers.Services.Vpn.VpnBootstrapHostedService>();

// Auto-update (opt-in) moved to Modules/ImageUpdate (RoadToSAP Phase 1 §3.7).

// Webhooks (IWebhookService) moved to Modules/Webhooks (RoadToSAP Phase 1). Core keeps a NoopWebhookService
// default (registered above) for the inbound /api/webhooks endpoint when the module is off.

// Log search + monitor (ILogSearchService, hosted LogMonitorService) moved to Modules/LogMonitor
// (RoadToSAP Phase 1). Core keeps a NoopLogMonitorService default (registered above) for when it's off.

// AI Chat
builder.Services.Configure<Whiskers.Configuration.AiChatSettings>(builder.Configuration.GetSection(Whiskers.Configuration.AiChatSettings.SectionName));
builder.Services.AddHttpClient<Whiskers.Services.AiChat.AiChatService>();
builder.Services.AddSingleton<Whiskers.Services.AiChat.AiChatService>();
builder.Services.AddSingleton<Whiskers.Services.AiChat.IAiChatService>(sp => sp.GetRequiredService<Whiskers.Services.AiChat.AiChatService>());
builder.Services.AddSingleton<Whiskers.Services.AiChat.IChatHistoryStore, Whiskers.Services.AiChat.ChatHistoryStore>();

// Agent (acting multi-provider agent with inescapable guardrails)
builder.Services.Configure<Whiskers.Configuration.AgentSettings>(
    builder.Configuration.GetSection(Whiskers.Configuration.AgentSettings.SectionName));
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentToolRegistry,
    Whiskers.Services.Agent.AgentToolRegistry>();
// The guardrail engine is stateless → a shared default rule set is enough.
builder.Services.AddSingleton<Whiskers.Services.Agent.Guardrails.IAgentGuardrailEngine>(
    Whiskers.Services.Agent.Guardrails.GuardrailEngine.CreateDefault());
builder.Services.AddSingletonWithInterface<Whiskers.Services.Agent.Guardrails.GuardrailStore, Whiskers.Services.Agent.Guardrails.IGuardrailStore>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Guardrails.IGuardrailRuleCatalog,
    Whiskers.Services.Agent.Guardrails.GuardrailRuleCatalog>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Providers.IAgentProviderFactory,
    Whiskers.Services.Agent.Providers.AgentProviderFactory>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentToolCatalog,
    Whiskers.Services.Agent.AgentToolCatalog>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentToolInvoker,
    Whiskers.Services.Agent.AgentToolInvoker>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentPrincipalResolver,
    Whiskers.Services.Agent.AgentPrincipalResolver>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Approvals.IApprovalStore,
    Whiskers.Services.Agent.Approvals.ApprovalStore>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Approvals.IApprovalCoordinator,
    Whiskers.Services.Agent.Approvals.ApprovalCoordinator>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Chat.IChatWidgetParser,
    Whiskers.Services.Agent.Chat.ChatWidgetParser>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentService,
    Whiskers.Services.Agent.AgentService>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IClaudeCodeRuntime,
    Whiskers.Services.Agent.ClaudeCodeRuntime>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentTranscriptStore,
    Whiskers.Services.Agent.AgentTranscriptStore>();
builder.Services.AddSingleton<Whiskers.Services.Agent.IAgentSettingsStore,
    Whiskers.Services.Agent.AgentSettingsStore>();

// AI triggers (autonomous agent runs on events)
builder.Services.AddSingletonWithInterface<Whiskers.Services.Agent.Triggers.AiTriggerStore, Whiskers.Services.Agent.Triggers.IAiTriggerStore>();
builder.Services.AddSingleton<Whiskers.Services.Agent.Triggers.IAiTriggerDispatcher,
    Whiskers.Services.Agent.Triggers.AiTriggerDispatcher>();

// Audit log
builder.Services.AddSingleton<Whiskers.Services.AuditLog.IAuditLogService, Whiskers.Services.AuditLog.AuditLogService>();

// MCP/agent observability (Agent History)
builder.Services.AddSingleton<Whiskers.Services.Observability.IMcpCallLogStore, Whiskers.Services.Observability.McpCallLogStore>();

// Volume backups (IVolumeBackupService) moved to Modules/VolumeBackups (RoadToSAP Phase 1).
// Core keeps a NoopVolumeBackupService default (registered above) for when it's off.

// MCP Server + Permissions
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Whiskers.Mcp.IMcpApiKeyStore, McpApiKeyStore>();
builder.Services.AddSingleton<Whiskers.Services.Mcp.IMcpPermissionService, McpPermissionService>();
// MCP tools come from the enabled modules (RoadToSAP Phase 1) instead of a fixed .WithTools<>() list, so a
// disabled module's tools drop off the MCP surface automatically. Same 11 tool types + order today (all
// carried by AllInOnePseudoModule.McpToolTypes), so this is behaviour-neutral.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools(modules.SelectMany(m => m.McpToolTypes).ToArray());

// MudBlazor
// Localization (F2 i18n): resource tables live in Resources/*.resx; en is the default/fallback
// culture, de is a full translation. Consumers inject IStringLocalizer<SharedResource>.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddMudServices();

// Authentication — cookie session + optional federated providers (Google and/or generic OIDC),
// or full bypass for trusted LAN-only deployments.
var authDisabled = builder.Configuration.GetValue<bool>("Auth:Disabled");
var googleAuthSection = builder.Configuration.GetSection(GoogleAuthSettings.SectionName);
var googleClientId = googleAuthSection["ClientId"];

builder.Services.Configure<OidcSettings>(builder.Configuration.GetSection(OidcSettings.SectionName));
var oidcSection = builder.Configuration.GetSection(OidcSettings.SectionName);
var oidcEnabled = oidcSection.GetValue<bool>("Enabled") && !string.IsNullOrWhiteSpace(oidcSection["Authority"]);

// Shared gate: after a provider authenticates the user, enforce the email whitelist before issuing
// the local cookie. Used by both Google and OIDC (both deliver a TicketReceivedContext).
Task WhitelistGate(Microsoft.AspNetCore.Authentication.TicketReceivedContext context)
{
    var whitelist = context.HttpContext.RequestServices.GetRequiredService<Whiskers.Services.Auth.IWhitelistService>();
    var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
    if (!whitelist.IsEmailAllowed(email))
    {
        context.Response.Redirect(configuredPathBase + "/login?error=unauthorized");
        context.HandleResponse();
    }
    return Task.CompletedTask;
}

if (authDisabled)
{
    // No real auth — every request is signed in as a local user.
    // ONLY safe on a trusted private network. Set via Auth__Disabled=true env var.
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options => { options.LoginPath = "/login"; });
}
else
{
    // Cookie is the session scheme; providers are layered on top. No default challenge scheme is
    // forced — unauthenticated users land on the cookie LoginPath (/login), which renders one button
    // per configured provider (/login-google, /login-oidc).
    var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.ExpireTimeSpan = TimeSpan.FromHours(24);

            // Re-check the whitelist on every request, not just at login, so an email that has been
            // removed from a NON-empty whitelist loses access on its next request instead of riding
            // out its 24h cookie. FAIL-OPEN by design: the synthetic AuthDisabled principal is exempt,
            // and an empty/disabled whitelist or any error allows through — matching WhitelistService's
            // "empty ⇒ allow all" semantics. The only new rejection is an email explicitly dropped from
            // a populated whitelist.
            options.Events.OnValidatePrincipal = context =>
            {
                try
                {
                    // Never touch the trusted-LAN auth-bypass principal.
                    if (context.Principal?.Identity?.AuthenticationType == "AuthDisabled")
                        return Task.CompletedTask;

                    var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
                    // No email ⇒ leave as-is; the login flow already decided this principal was OK.
                    if (string.IsNullOrEmpty(email))
                        return Task.CompletedTask;

                    var whitelist = context.HttpContext.RequestServices.GetRequiredService<Whiskers.Services.Auth.IWhitelistService>();
                    // IsEmailAllowed returns true when the whitelist is empty/disabled (fail-open).
                    if (!whitelist.IsEmailAllowed(email))
                    {
                        context.RejectPrincipal();
                        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                }
                catch
                {
                    // Fail-open: a transient error must never lock out a legitimate user.
                }
                return Task.CompletedTask;
            };
        });

    if (!string.IsNullOrWhiteSpace(googleClientId))
    {
        authBuilder.AddGoogle(options =>
        {
            options.ClientId = googleClientId!;
            options.ClientSecret = googleAuthSection["ClientSecret"] ?? "";
            options.Events.OnTicketReceived = WhitelistGate;
        });
    }

    if (oidcEnabled)
    {
        authBuilder.AddOpenIdConnect("oidc", oidcSection["DisplayName"] ?? "SSO", options =>
        {
            options.Authority = oidcSection["Authority"];
            options.ClientId = oidcSection["ClientId"];
            options.ClientSecret = oidcSection["ClientSecret"];
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            var oidcRequireHttps = oidcSection.GetValue("RequireHttpsMetadata", true);
            options.RequireHttpsMetadata = oidcRequireHttps;
            // Relative paths — combined with UsePathBase + forwarded headers they resolve to
            // {PathBase}/signin-oidc etc. Register that full URL as the redirect URI at the IdP.
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";

            // Survive plain-HTTP / LAN deployments: form_post is a cross-site POST whose SameSite
            // correlation/nonce cookies would be dropped without HTTPS ("Correlation failed").
            // Query response mode makes the callback a top-level GET, so Lax cookies are sent.
            options.ResponseMode = "query";
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            if (!oidcRequireHttps)
            {
                // Plain-HTTP LAN: these cookies must NOT be Secure, otherwise the browser stores
                // but never sends them back over http → "Correlation failed" on the callback.
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.None;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.None;
            }

            options.Scope.Clear();
            foreach (var s in (oidcSection["Scopes"] ?? "openid profile email")
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                options.Scope.Add(s);

            var emailClaim = oidcSection["EmailClaim"] ?? "email";
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, emailClaim);
            options.TokenValidationParameters.NameClaimType = emailClaim;

            options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
            {
                OnTicketReceived = WhitelistGate
            };
        });
    }
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Blazor + SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 64 * 1024;
});

// Startup initializers — the loop after Build() runs each IInitializable's async warm-up in Order,
// replacing the previously hand-wired InitializeAsync calls. Order values live on the services.
builder.Services.AddInitializable<Whiskers.Services.Auth.IWhitelistService>();              // 10
builder.Services.AddInitializable<Whiskers.Services.Auth.IRoleService>();                   // 20
builder.Services.AddInitializable<Whiskers.Services.Notifications.IContainerNotificationPrefsService>(); // 30
builder.Services.AddInitializable<Whiskers.Services.Vault.IVaultService>();                 // 40
builder.Services.AddInitializable<Whiskers.Services.ServerConfig.IServerConfigService>();   // 50
builder.Services.AddInitializable<Whiskers.Mcp.IMcpApiKeyStore>();                          // 60
builder.Services.AddInitializable<Whiskers.Services.Mcp.IMcpPermissionService>();           // 70
builder.Services.AddInitializable<Whiskers.Services.Agent.Guardrails.GuardrailStore>();     // 80
builder.Services.AddInitializable<Whiskers.Services.Agent.Triggers.AiTriggerStore>();       // 90

// Nav registry from the enabled modules' merged NavItems (modules are discovered near the top of
// Program.cs, next to where their services and MCP tools are wired).
builder.Services.AddSingleton<Whiskers.Modules.IModuleRegistry>(
    new Whiskers.Modules.ModuleRegistry(
        modules.SelectMany(m => m.NavItems).ToList(),
        modules.Select(m => m.Id)));

var app = builder.Build();

// Forwarded headers MUST come first so scheme (https) is detected before anything else
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
// Trust forwarded headers ONLY from configured proxy networks. Leaving both lists empty makes
// ASP.NET Core skip the known-proxy check entirely (trust-all), which lets any client spoof
// X-Forwarded-For and corrupt the SourceIp recorded for webhooks/audit. Defaults to loopback +
// RFC1918 + Tailscale CGNAT; override via ForwardedHeaders:TrustedNetworks (array of CIDRs).
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
foreach (var network in ForwardedHeadersConfig.ParseTrustedNetworks(
             app.Configuration.GetSection("ForwardedHeaders:TrustedNetworks").Get<string[]>()))
{
    forwardedHeadersOptions.KnownIPNetworks.Add(network);
}
app.UseForwardedHeaders(forwardedHeadersOptions);

// Support reverse proxy with subpath
if (!string.IsNullOrEmpty(configuredPathBase))
{
    app.UsePathBase(configuredPathBase);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Request localization (F2 i18n): pick culture from the user's cookie, then Accept-Language, defaulting
// to English. Runs early so Blazor rendering sees the right culture. Additive — it does NOT reorder the
// auth middleware (UseAntiforgery → UseAuthentication → UseAuthorization) below.
var supportedCultures = new[] { "en", "de" };
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures));

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();

// Auth bypass for trusted LAN deployments — inject a synthetic authenticated
// principal so [Authorize]-protected pages render without a login flow.
if (authDisabled)
{
    app.Use(async (ctx, next) =>
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "local"),
            new Claim(ClaimTypes.Email, "local@serverwatch.local"),
            new Claim(ClaimTypes.NameIdentifier, "local-user"),
        }, authenticationType: "AuthDisabled");
        ctx.User = new ClaimsPrincipal(identity);
        await next();
    });
}

app.UseMiddleware<Whiskers.Mcp.McpBearerAuthMiddleware>();
app.UseAuthorization();

// Liveness/readiness probes for orchestrators (Docker HEALTHCHECK, K8s probes). Anonymous and
// additive — this does NOT change the auth middleware order. The response body is the status word
// only (no check names, descriptions or exceptions), so nothing internal leaks. /health is the
// Blazor UI page, so these use /healthz + /readyz.
Func<HttpContext, HealthReport, Task> writeHealthStatus = (ctx, report) =>
{
    ctx.Response.ContentType = "text/plain";
    return ctx.Response.WriteAsync(report.Status.ToString());
};
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false,                       // liveness: process is up; don't gate on dependencies
    ResponseWriter = writeHealthStatus
}).AllowAnonymous();
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = writeHealthStatus
}).AllowAnonymous();

// Language switch (F2 i18n): write the culture cookie, then bounce back. Anonymous + additive; the full
// reload restarts the Blazor circuit so it renders in the new culture. LocalRedirect blocks open-redirects.
app.MapGet("/set-culture", (string? culture, string? redirect, HttpContext ctx) =>
{
    if (culture is "en" or "de")
    {
        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" });
    }
    return Results.LocalRedirect(string.IsNullOrEmpty(redirect) ? "/" : redirect);
}).AllowAnonymous();

// Auth endpoints
app.MapGet("/login-google", async (HttpContext ctx) =>
{
    await ctx.ChallengeAsync(GoogleDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = configuredPathBase + "/"
    });
});

app.MapGet("/login-oidc", async (HttpContext ctx) =>
{
    await ctx.ChallengeAsync("oidc", new AuthenticationProperties
    {
        RedirectUri = configuredPathBase + "/"
    });
});

app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect(configuredPathBase + "/login");
});

// SignalR hubs
app.MapHub<ContainerHub>("/hubs/containers");

// Records external/direct MCP tool calls (callers that bypass the in-process agent) for Agent-History.
app.UseMiddleware<Whiskers.Mcp.McpCallLogMiddleware>();

// MCP endpoint with API key auth (new permission system)
app.MapMcp("/mcp").RequireAuthorization(policy =>
    policy.RequireAssertion(context =>
    {
        var httpContext = context.Resource as HttpContext;
        var permService = httpContext?.RequestServices.GetService<Whiskers.Services.Mcp.IMcpPermissionService>();
        var authHeader = httpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer "))
        {
            var key = authHeader["Bearer ".Length..];
            // Validate via new permission service (or legacy store for backwards compat)
            if (permService?.ValidateKey(key) != null) return true;
            var legacyStore = httpContext?.RequestServices.GetService<Whiskers.Mcp.IMcpApiKeyStore>();
            return legacyStore?.ValidateKey(key) == true;
        }
        // Also allow authenticated web users
        return context.User.Identity?.IsAuthenticated == true;
    }));

// Webhook API endpoint (no auth — uses HMAC signature validation)
app.MapPost("/api/webhooks/{webhookId}", async (string webhookId, HttpContext ctx) =>
{
    var webhookService = ctx.RequestServices.GetRequiredService<Whiskers.Services.Webhooks.IWebhookService>();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
    var sourceIp = ctx.Connection.RemoteIpAddress?.ToString();

    var (success, output) = await webhookService.TriggerAsync(webhookId, signature, body, sourceIp);
    return success ? Results.Ok(new { status = "ok", output }) : Results.BadRequest(new { status = "error", output });
});

// Prometheus metrics endpoint. Gated by a static scrape token (Metrics:ScrapeToken) because the
// payload is the full multi-server container inventory. With no token configured the endpoint stays
// disabled (opt-in) rather than served openly — the safe default for the hardened host-network profile.
var metricsScrapeToken = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<MetricsSettings>>().Value.ScrapeToken;
app.MapGet("/metrics", async (HttpContext ctx) =>
{
    switch (MetricsScrapeAuth.Check(metricsScrapeToken, ctx.Request.Headers.Authorization.ToString()))
    {
        case MetricsScrapeAuthResult.Disabled:
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        case MetricsScrapeAuthResult.Unauthorized:
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
    }

    var docker = ctx.RequestServices.GetRequiredService<Whiskers.Services.Docker.IDockerService>();
    var sb = new System.Text.StringBuilder();

    try
    {
        var containers = await docker.ListAllContainersAsync(all: true);

        // Container counts
        sb.AppendLine($"# HELP serverwatch_containers_total Total number of containers");
        sb.AppendLine($"# TYPE serverwatch_containers_total gauge");
        sb.AppendLine($"serverwatch_containers_total {containers.Count}");

        sb.AppendLine($"# HELP serverwatch_containers_running Number of running containers");
        sb.AppendLine($"# TYPE serverwatch_containers_running gauge");
        sb.AppendLine($"serverwatch_containers_running {containers.Count(c => c.State == "running")}");

        sb.AppendLine($"# HELP serverwatch_containers_unhealthy Number of unhealthy containers");
        sb.AppendLine($"# TYPE serverwatch_containers_unhealthy gauge");
        sb.AppendLine($"serverwatch_containers_unhealthy {containers.Count(c => c.HealthStatus == "unhealthy")}");

        // Per-container stats
        sb.AppendLine($"# HELP serverwatch_container_cpu_percent CPU usage percentage per container");
        sb.AppendLine($"# TYPE serverwatch_container_cpu_percent gauge");
        sb.AppendLine($"# HELP serverwatch_container_memory_bytes Memory usage in bytes per container");
        sb.AppendLine($"# TYPE serverwatch_container_memory_bytes gauge");
        sb.AppendLine($"# HELP serverwatch_container_up Whether container is running (1) or not (0)");
        sb.AppendLine($"# TYPE serverwatch_container_up gauge");

        foreach (var c in containers)
        {
            var labels = $"name=\"{c.Name}\",image=\"{c.Image}\",server=\"{c.ServerName}\",project=\"{c.ComposeProject}\"";
            sb.AppendLine($"serverwatch_container_up{{{labels}}} {(c.State == "running" ? 1 : 0)}");

            if (c.LatestStats != null)
            {
                sb.AppendLine($"serverwatch_container_cpu_percent{{{labels}}} {c.LatestStats.CpuPercent:F2}");
                sb.AppendLine($"serverwatch_container_memory_bytes{{{labels}}} {c.LatestStats.MemoryUsageBytes}");
            }
        }

        // Server info
        try
        {
            var serverInfo = await docker.GetServerSystemInfoAsync();
            sb.AppendLine($"# HELP serverwatch_server_cpu_count Number of CPU cores");
            sb.AppendLine($"# TYPE serverwatch_server_cpu_count gauge");
            sb.AppendLine($"serverwatch_server_cpu_count {serverInfo.CpuCount}");
            sb.AppendLine($"# HELP serverwatch_server_memory_total_bytes Total server memory");
            sb.AppendLine($"# TYPE serverwatch_server_memory_total_bytes gauge");
            sb.AppendLine($"serverwatch_server_memory_total_bytes {serverInfo.MemoryTotalBytes}");
        }
        catch { }
    }
    catch (Exception ex)
    {
        sb.AppendLine($"# ERROR: {ex.Message}");
    }

    ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
    await ctx.Response.WriteAsync(sb.ToString());
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Run each IInitializable's async warm-up in ascending Order (whitelist → roles → notif-prefs →
// vault → server-config → MCP key store → MCP permissions → guardrails → AI triggers). Replaces the
// previously hand-wired InitializeAsync calls; the order lives on each service (see IInitializable).
foreach (var initializable in app.Services.GetServices<Whiskers.Services.IInitializable>().OrderBy(i => i.Order))
    await initializable.InitializeAsync(CancellationToken.None);

// Bring the SQLite metrics database up to the current schema via EF Core migrations. On a legacy
// EnsureCreated database (no migration history) this baselines onto migrations without recreating
// existing tables — see Services/Persistence/DatabaseInitializer and docs/adr/0003.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
    var dbLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
    await Whiskers.Services.Persistence.DatabaseInitializer.InitializeAsync(db, dbLogger);
}

app.Run();

// Normal boot returns 0 on shutdown; the --migrate-to-postgres branch above returns its own exit code.
return 0;
