using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Mcp;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Mcp;
using Whiskers.Services.HealthMonitor;
using Whiskers.Services.Metrics;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.HealthChecks;

namespace Whiskers.Startup;

/// <summary>Service-registration halves of the composition root, split out of Program.cs so it stays a thin
/// orchestrator (RoadToSAP §6 DoD). Every registration is moved <b>verbatim</b> — same services, same order
/// where order matters (the module no-op defaults are still registered before the module loop). The
/// security-sensitive authentication wiring lives separately in <see cref="WhiskersAuthenticationExtensions"/>,
/// and the HTTP pipeline in <see cref="WhiskersPipelineExtensions"/>.</summary>
public static class WhiskersHostingExtensions
{
    /// <summary>UI-writable config layers + the data-path singleton + data-protection keys (Program.cs bootstrap).</summary>
    public static void AddWhiskersConfiguration(this WebApplicationBuilder builder, DataPathOptions dataPaths)
    {
        // UI-writable agent provider settings (overrides only Agent:* keys; reloadOnChange → IOptionsMonitor
        // picks up UI changes without a restart). As the last source, so the UI takes precedence over env/appsettings.
        builder.Configuration.AddJsonFile(dataPaths.AgentSettingsJson, optional: true, reloadOnChange: true);
        // UI-writable settings for all other sections (Mattermost, Matrix, HealthMonitor, CveMonitor,
        // ImageUpdate, MetricAlert, …). Last layer → overrides env; reloadOnChange → applied live.
        builder.Configuration.AddJsonFile(dataPaths.AppSettingsJson, optional: true, reloadOnChange: true);

        // Register the data-path resolver so every consumer can inject it (DI fills the optional
        // DataPathOptions constructor parameter from this instance).
        builder.Services.AddSingleton(dataPaths);

        // Persist data protection keys so antiforgery tokens survive container restarts
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataPaths.KeysDir))
            .SetApplicationName("ServerWatch");
    }

    /// <summary>The module pipeline (RoadToSAP Phase 1): Core no-op defaults registered BEFORE the module loop,
    /// the enabled modules' ConfigureServices, the MCP server fed from the modules' tool types, and the module
    /// registry. Order preserved verbatim from Program.cs.</summary>
    public static void AddWhiskersModules(this WebApplicationBuilder builder)
    {
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
        // And for webhooks: the inbound /api/webhooks/{id} endpoint (in the pipeline, in Core) resolves IWebhookService
        // per request, so it needs a default when the Webhooks module is off. The module registers the real service in
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
        // Since C12 the Dashboard also consumes IAutoUpdateService (the manual-rollback button + capturing a snapshot
        // before a manual update), so it likewise needs a default when the ImageUpdate module is off. The module
        // registers the real hosted AutoUpdateService in the loop below and wins. (RoadToSAP §2.1 / changeme C12)
        builder.Services.AddSingleton<Whiskers.Services.AutoUpdate.IAutoUpdateService, Whiskers.Services.AutoUpdate.NoopAutoUpdateService>();
        // And for AI triggers: the notification composite (Modules/Notifications) lazily resolves IAiTriggerDispatcher
        // on every event (to avoid a DI cycle), so it needs a default when the Agent module is off. The Agent module
        // registers the real dispatcher in the loop below and wins. (RoadToSAP §2.1 / §3.8)
        builder.Services.AddSingleton<Whiskers.Services.Agent.Triggers.IAiTriggerDispatcher, Whiskers.Services.Agent.Triggers.NoopAiTriggerDispatcher>();

        // Module pipeline (RoadToSAP Phase 1). Discover enabled modules early (Features:<id>:Enabled overrides each
        // module's default) so their services, MCP tools and navigation all come from one list. Each enabled module's
        // ConfigureServices moves its former inline Program.cs registrations here verbatim.
        var modules = Whiskers.Modules.ModuleCatalog.DiscoverEnabled(builder.Configuration);
        foreach (var module in modules)
            module.ConfigureServices(builder.Services, builder.Configuration);

        // MCP Server + Permissions. Tools come from the enabled modules (RoadToSAP Phase 1) instead of a fixed
        // .WithTools<>() list, so a disabled module's tools drop off the MCP surface automatically.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<Whiskers.Mcp.IMcpApiKeyStore, McpApiKeyStore>();
        builder.Services.AddSingleton<Whiskers.Services.Mcp.IMcpPermissionService, McpPermissionService>();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools(modules.SelectMany(m => m.McpToolTypes).ToArray());

        // Nav + tool registry from the enabled modules' merged metadata.
        builder.Services.AddSingleton<Whiskers.Modules.IModuleRegistry>(
            new Whiskers.Modules.ModuleRegistry(
                modules.SelectMany(m => m.NavItems).ToList(),
                modules.SelectMany(m => m.McpToolTypes).ToList(),
                modules.Select(m => m.Id)));
    }

    /// <summary>All the remaining Core service registrations moved verbatim from Program.cs — Docker, health,
    /// metrics, VPN, database, audit/observability, and the startup-initializer registrations. (Feature services
    /// that moved into modules keep only their Core no-op defaults, registered in <see cref="AddWhiskersModules"/>.)</summary>
    public static void AddWhiskersCoreServices(this WebApplicationBuilder builder, DataPathOptions dataPaths)
    {
        // Configuration
        builder.Services.Configure<DockerSettings>(builder.Configuration.GetSection(DockerSettings.SectionName));
        builder.Services.Configure<GoogleAuthSettings>(builder.Configuration.GetSection(GoogleAuthSettings.SectionName));
        builder.Services.Configure<HealthMonitorSettings>(builder.Configuration.GetSection(HealthMonitorSettings.SectionName));

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
        // when the module is off.
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

        // Auth whitelist + roles
        builder.Services.AddSingleton<Whiskers.Services.Auth.IWhitelistService, WhitelistService>();
        builder.Services.AddSingleton<Whiskers.Services.Auth.IRoleService, Whiskers.Services.Auth.RoleService>();
        builder.Services.AddSingleton<Whiskers.Services.Setup.ISetupStateService, Whiskers.Services.Setup.SetupStateService>();
        // Track B.1: backend-neutral workload seam. Dispatches per server (today always Docker; the
        // Kubernetes provider joins in Track B.2). Consumers migrate onto it incrementally.
        builder.Services.AddSingleton<Whiskers.Services.Workloads.IWorkloadProviderFactory, Whiskers.Services.Workloads.WorkloadProviderFactory>();
        // W3.4: static production-readiness checklist (Settings panel). Read-only checks.
        builder.Services.AddSingleton<Whiskers.Services.Setup.IProductionReadinessService, Whiskers.Services.Setup.ProductionReadinessService>();
        // Per-circuit current-user/role resolver (scoped — depends on the scoped AuthenticationStateProvider)
        builder.Services.AddScoped<Whiskers.Services.Auth.ICurrentUserService, Whiskers.Services.Auth.CurrentUserService>();

        // Notification prefs per container
        builder.Services.AddSingleton<Whiskers.Services.Notifications.IContainerNotificationPrefsService, Whiskers.Services.Notifications.ContainerNotificationPrefsService>();

        // Secret vault
        builder.Services.AddSingleton<Whiskers.Services.Vault.IVaultService, Whiskers.Services.Vault.VaultService>();

        // Host command execution + server management. IHostCommandExecutor stays in Core (shared by many services).
        builder.Services.AddSingleton<IHostCommandExecutor, HostCommandExecutor>();
        builder.Services.AddSingleton<Whiskers.Services.Onboarding.IOnboardingService, Whiskers.Services.Onboarding.OnboardingService>();

        // Metrics database — SQLite (zero-config default) or PostgreSQL, selected by Database:Provider
        // (WHISKERS_DB_PROVIDER / _CONNECTION[_FILE]). SQLite default is byte-identical to before. (stableDB.md)
        builder.AddWhiskersDatabase(dataPaths);

        // Liveness/readiness health checks, surfaced at /healthz and /readyz in the pipeline. Only readiness
        // checks carry the "ready" tag; /healthz stays dependency-free.
        builder.Services.AddHealthChecks()
            .AddCheck<DbReadyCheck>("db", tags: new[] { "ready" })
            .AddCheck<ServerConfigReadyCheck>("serverconfig", tags: new[] { "ready" })
            // F3: drain readiness while a restore is staged and the app is about to restart. "ready" tag only,
            // so /healthz (the container liveness probe) is unaffected.
            .AddCheck<MaintenanceReadyCheck>("maintenance", tags: new[] { "ready" });
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

        // Audit log
        builder.Services.AddSingleton<Whiskers.Services.AuditLog.IAuditLogService, Whiskers.Services.AuditLog.AuditLogService>();

        // MCP/agent observability (Agent History)
        builder.Services.AddSingleton<Whiskers.Services.Observability.IMcpCallLogStore, Whiskers.Services.Observability.McpCallLogStore>();

        // F3 self-backup/restore + maintenance mode. Both are Core (always present): the scheduler's
        // SelfBackup task and the Settings "Backup & Restore" panel depend on IBackupService directly, and the
        // maintenance flag gates requests during a restore. No module no-op — these never turn off.
        builder.Services.AddSingleton<Whiskers.Services.Maintenance.IMaintenanceStateService, Whiskers.Services.Maintenance.MaintenanceStateService>();
        builder.Services.AddSingleton<Whiskers.Services.Backup.IBackupService, Whiskers.Services.Backup.BackupService>();

        // Startup initializers — the loop after Build() runs each IInitializable's async warm-up in Order,
        // replacing the previously hand-wired InitializeAsync calls. Order values live on the services.
        builder.Services.AddInitializable<Whiskers.Services.Auth.IWhitelistService>();              // 10
        builder.Services.AddInitializable<Whiskers.Services.Auth.IRoleService>();                   // 20
        builder.Services.AddInitializable<Whiskers.Services.Setup.ISetupStateService>();            // 25 (after roles)
        builder.Services.AddInitializable<Whiskers.Services.Notifications.IContainerNotificationPrefsService>(); // 30
        builder.Services.AddInitializable<Whiskers.Services.Vault.IVaultService>();                 // 40
        builder.Services.AddInitializable<Whiskers.Services.ServerConfig.IServerConfigService>();   // 50
        builder.Services.AddInitializable<Whiskers.Mcp.IMcpApiKeyStore>();                          // 60
        builder.Services.AddInitializable<Whiskers.Services.Mcp.IMcpPermissionService>();           // 70
        // GuardrailStore (80) + AiTriggerStore (90) warm-ups moved to Modules/Agent (RoadToSAP §3.8): the module
        // registers its own AddInitializable when enabled, so they run in Order then and drop out when the agent is off.
    }

    /// <summary>UI framework registrations: localization (F2 i18n), MudBlazor, Blazor interactive server
    /// components and SignalR. Moved verbatim from Program.cs.</summary>
    public static void AddWhiskersUi(this WebApplicationBuilder builder)
    {
        // Localization (F2 i18n): resource tables live in Resources/*.resx; en is the default/fallback
        // culture, de is a full translation. Consumers inject IStringLocalizer<SharedResource>.
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        builder.Services.AddMudServices();

        // Blazor + SignalR
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 64 * 1024;
        });
    }
}
