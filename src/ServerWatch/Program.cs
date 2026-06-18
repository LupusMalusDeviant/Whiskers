using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using ServerWatch.Components;
using ServerWatch.Configuration;
using ServerWatch.Hubs;
using ServerWatch.Mcp;
using ServerWatch.Mcp.Tools;
using ServerWatch.Services.Deployment;
using ServerWatch.Services.Docker;
using ServerWatch.Services.HealthMonitor;
using ServerWatch.Services.Metrics;
using ServerWatch.Services.Notifications;
using ServerWatch.Services.Persistence;
using ServerWatch.Services.Server;
using ServerWatch.Services.Terminal;
using ServerWatch.Services.Auth;
using ServerWatch.Services.ServerConfig;
using ServerWatch.Services.ImageUpdate;
using ServerWatch.Services.Cve;
using ServerWatch.Services.Mcp;
using ServerWatch.Services.Coolify;
using ServerWatch.Services.Hetzner;
using ServerWatch.Services.Hostinger;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Path base for reverse proxy subpath
var configuredPathBase = builder.Configuration["PathBase"] ?? "";

// Persist data protection keys so antiforgery tokens survive container restarts
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/data/keys"))
    .SetApplicationName("ServerWatch");

// Configuration
builder.Services.Configure<DockerSettings>(builder.Configuration.GetSection(DockerSettings.SectionName));
builder.Services.Configure<MattermostSettings>(builder.Configuration.GetSection(MattermostSettings.SectionName));
builder.Services.Configure<GoogleAuthSettings>(builder.Configuration.GetSection(GoogleAuthSettings.SectionName));
builder.Services.Configure<TerminalSettings>(builder.Configuration.GetSection(TerminalSettings.SectionName));
builder.Services.Configure<HealthMonitorSettings>(builder.Configuration.GetSection(HealthMonitorSettings.SectionName));
builder.Services.Configure<ServerWatch.Configuration.MatrixSettings>(builder.Configuration.GetSection(ServerWatch.Configuration.MatrixSettings.SectionName));

// Server config + Docker services
builder.Services.AddSingleton<ServerConfigService>();
builder.Services.AddSingleton<SshTunnelManager>();
builder.Services.AddSingleton<DockerConnectionManager>();
builder.Services.AddSingleton<IDockerService, DockerService>();

// Health monitoring
builder.Services.AddSingleton<IHealthStore, InMemoryHealthStore>();
builder.Services.AddHostedService<ContainerHealthMonitor>();

// Notifications (Mattermost + Matrix via composite)
builder.Services.AddHttpClient<MattermostNotificationService>();
builder.Services.AddSingleton<MattermostNotificationService>();
builder.Services.AddHttpClient<MatrixNotificationService>();
builder.Services.AddSingleton<MatrixNotificationService>();
builder.Services.AddSingleton<INotificationService, CompositeNotificationService>();

// Terminal
builder.Services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();

// Deployment
builder.Services.AddScoped<IDeploymentService, DeploymentService>();

// Image update checking
builder.Services.Configure<ImageUpdateSettings>(builder.Configuration.GetSection("ImageUpdate"));
builder.Services.AddHttpClient<RegistryClient>();
builder.Services.AddSingleton<ImageUpdateStore>();
builder.Services.AddSingleton<RegistryClient>();
builder.Services.AddHostedService<ImageUpdateChecker>();

// CVE monitoring (containers via Trivy + OS via apt)
builder.Services.Configure<CveMonitorSettings>(builder.Configuration.GetSection(CveMonitorSettings.SectionName));
builder.Services.AddSingleton<CveFindingsStore>();
builder.Services.AddSingleton<OsCveScanner>();
builder.Services.AddSingleton<TrivyScanner>();
// Registered as Singleton AND HostedService — same instance — so UI can trigger manual scans.
builder.Services.AddSingleton<CveMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CveMonitorService>());

// Auth whitelist + roles
builder.Services.AddSingleton<WhitelistService>();
builder.Services.AddSingleton<ServerWatch.Services.Auth.RoleService>();

// Notification prefs per container
builder.Services.AddSingleton<ServerWatch.Services.Notifications.ContainerNotificationPrefsService>();

// Config export
builder.Services.AddSingleton<ServerWatch.Services.ConfigExport.ConfigExportService>();

// Secret vault
builder.Services.AddSingleton<ServerWatch.Services.Vault.VaultService>();

// Coolify integration
builder.Services.AddSingleton<CoolifyConfigService>();
builder.Services.AddHttpClient<ICoolifyService, CoolifyApiService>();

// Cloud provider integrations (per-server credentials, provider-agnostic dispatch)
builder.Services.AddHttpClient<IHetznerService, HetznerApiService>();
builder.Services.AddHttpClient<IHostingerService, HostingerApiService>();
builder.Services.AddSingleton<ServerWatch.Services.Cloud.CloudControlService>();

// Host command execution + server management
builder.Services.AddSingleton<IHostCommandExecutor, HostCommandExecutor>();
builder.Services.AddSingleton<FirewallService>();
builder.Services.AddSingleton<NginxService>();
builder.Services.AddSingleton<SystemdService>();
builder.Services.AddSingleton<SslCertService>();

// SQLite metrics database
builder.Services.AddDbContext<MetricsDbContext>(options =>
    options.UseSqlite("Data Source=/app/data/metrics.db"),
    ServiceLifetime.Transient);
builder.Services.Configure<MetricsSettings>(builder.Configuration.GetSection(MetricsSettings.SectionName));
builder.Services.AddSingleton<MetricsQueryService>();
builder.Services.AddHostedService<MetricsCollectorService>();

// Database service
builder.Services.AddSingleton<ServerWatch.Services.Database.IDatabaseService, ServerWatch.Services.Database.DatabaseService>();

// Scheduler
builder.Services.AddSingleton<ServerWatch.Services.Scheduler.TaskExecutor>();
builder.Services.AddSingleton<ServerWatch.Services.Scheduler.SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerWatch.Services.Scheduler.SchedulerService>());

// Resource recommendations
builder.Services.AddSingleton<ServerWatch.Services.Recommendations.ResourceRecommendationService>();

// App templates
builder.Services.AddSingleton<ServerWatch.Services.Templates.TemplateService>();

// Auto-update (opt-in only)
builder.Services.AddSingleton<ServerWatch.Services.AutoUpdate.AutoUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerWatch.Services.AutoUpdate.AutoUpdateService>());

// Webhooks
builder.Services.AddSingleton<ServerWatch.Services.Webhooks.WebhookService>();

// Log monitoring
builder.Services.AddSingleton<ServerWatch.Services.LogMonitor.LogSearchService>();
builder.Services.AddSingleton<ServerWatch.Services.LogMonitor.LogMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerWatch.Services.LogMonitor.LogMonitorService>());

// AI Chat
builder.Services.Configure<ServerWatch.Configuration.AiChatSettings>(builder.Configuration.GetSection(ServerWatch.Configuration.AiChatSettings.SectionName));
builder.Services.AddHttpClient<ServerWatch.Services.AiChat.AiChatService>();
builder.Services.AddSingleton<ServerWatch.Services.AiChat.AiChatService>();
builder.Services.AddSingleton<ServerWatch.Services.AiChat.ChatHistoryStore>();

// Audit log
builder.Services.AddSingleton<ServerWatch.Services.AuditLog.IAuditLogService, ServerWatch.Services.AuditLog.AuditLogService>();

// Volume backups
builder.Services.AddSingleton<ServerWatch.Services.Backup.IVolumeBackupService, ServerWatch.Services.Backup.VolumeBackupService>();

// MCP Server + Permissions
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<McpApiKeyStore>();
builder.Services.AddSingleton<McpPermissionService>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<ContainerTools>()
    .WithTools<ServerTools>()
    .WithTools<MonitoringTools>()
    .WithTools<CoolifyTools>()
    .WithTools<CloudTools>()
    .WithTools<HetznerTools>()
    .WithTools<NetworkTools>()
    .WithTools<DatabaseTools>()
    .WithTools<SchedulerTools>()
    .WithTools<LogTools>()
    .WithTools<CveTools>();

// MudBlazor
builder.Services.AddMudServices();

// Authentication - Google OAuth (or full bypass for trusted LAN-only deployments)
var authDisabled = builder.Configuration.GetValue<bool>("Auth:Disabled");
var googleAuthSection = builder.Configuration.GetSection(GoogleAuthSettings.SectionName);
var googleClientId = googleAuthSection["ClientId"];

if (authDisabled)
{
    // No real auth — every request is signed in as a local user.
    // ONLY safe on a trusted private network. Set via Auth__Disabled=true env var.
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options => { options.LoginPath = "/login"; });
}
else if (!string.IsNullOrWhiteSpace(googleClientId))
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
    })
    .AddGoogle(options =>
    {
        options.ClientId = googleClientId!;
        options.ClientSecret = googleAuthSection["ClientSecret"] ?? "";
        options.Events.OnTicketReceived = context =>
        {
            var whitelist = context.HttpContext.RequestServices.GetRequiredService<WhitelistService>();
            var email = context.Principal?.FindFirstValue(ClaimTypes.Email);

            if (!whitelist.IsEmailAllowed(email))
            {
                context.Response.Redirect(configuredPathBase + "/login?error=unauthorized");
                context.HandleResponse();
            }
            return Task.CompletedTask;
        };
    });
}
else
{
    // No Google OAuth configured - use a simple cookie auth with no challenge
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
        });
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

var app = builder.Build();

// Forwarded headers MUST come first so scheme (https) is detected before anything else
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
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

app.UseMiddleware<ServerWatch.Mcp.McpBearerAuthMiddleware>();
app.UseAuthorization();

// Auth endpoints
app.MapGet("/login-google", async (HttpContext ctx) =>
{
    await ctx.ChallengeAsync(GoogleDefaults.AuthenticationScheme, new AuthenticationProperties
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
app.MapHub<TerminalHub>("/hubs/terminal");

// MCP endpoint with API key auth (new permission system)
app.MapMcp("/mcp").RequireAuthorization(policy =>
    policy.RequireAssertion(context =>
    {
        var httpContext = context.Resource as HttpContext;
        var permService = httpContext?.RequestServices.GetService<McpPermissionService>();
        var authHeader = httpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer "))
        {
            var key = authHeader["Bearer ".Length..];
            // Validate via new permission service (or legacy store for backwards compat)
            if (permService?.ValidateKey(key) != null) return true;
            var legacyStore = httpContext?.RequestServices.GetService<McpApiKeyStore>();
            return legacyStore?.ValidateKey(key) == true;
        }
        // Also allow authenticated web users
        return context.User.Identity?.IsAuthenticated == true;
    }));

// Webhook API endpoint (no auth — uses HMAC signature validation)
app.MapPost("/api/webhooks/{webhookId}", async (string webhookId, HttpContext ctx) =>
{
    var webhookService = ctx.RequestServices.GetRequiredService<ServerWatch.Services.Webhooks.WebhookService>();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
    var sourceIp = ctx.Connection.RemoteIpAddress?.ToString();

    var (success, output) = await webhookService.TriggerAsync(webhookId, signature, body, sourceIp);
    return success ? Results.Ok(new { status = "ok", output }) : Results.BadRequest(new { status = "error", output });
});

// Prometheus metrics endpoint (no auth)
app.MapGet("/metrics", async (HttpContext ctx) =>
{
    var docker = ctx.RequestServices.GetRequiredService<ServerWatch.Services.Docker.IDockerService>();
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

// Initialize services that need async startup
var whitelistService = app.Services.GetRequiredService<WhitelistService>();
await whitelistService.InitializeAsync();

var roleService = app.Services.GetRequiredService<ServerWatch.Services.Auth.RoleService>();
await roleService.InitializeAsync();

var notifPrefsService = app.Services.GetRequiredService<ServerWatch.Services.Notifications.ContainerNotificationPrefsService>();
await notifPrefsService.InitializeAsync();

var vaultService = app.Services.GetRequiredService<ServerWatch.Services.Vault.VaultService>();
await vaultService.InitializeAsync();

var serverConfigService = app.Services.GetRequiredService<ServerConfigService>();
await serverConfigService.InitializeAsync();

var mcpApiKeyStore = app.Services.GetRequiredService<McpApiKeyStore>();
await mcpApiKeyStore.InitializeAsync();

var mcpPermissionService = app.Services.GetRequiredService<McpPermissionService>();
await mcpPermissionService.InitializeAsync();

var coolifyConfigService = app.Services.GetRequiredService<CoolifyConfigService>();
await coolifyConfigService.InitializeAsync();

// Ensure SQLite database and tables are created (including new tables on existing DBs)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated only works on a fresh DB — manually create missing tables for existing DBs
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS "AuditLog" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL,
            "Actor" TEXT NOT NULL,
            "ActorType" TEXT NOT NULL,
            "Action" TEXT NOT NULL,
            "TargetType" TEXT NOT NULL,
            "TargetId" TEXT NOT NULL,
            "TargetName" TEXT NOT NULL,
            "Details" TEXT,
            "ServerId" TEXT,
            "Success" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Timestamp" ON "AuditLog" ("Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Action_Timestamp" ON "AuditLog" ("Action", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Actor" ON "AuditLog" ("Actor");

        CREATE TABLE IF NOT EXISTS "VolumeBackups" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "BackupId" TEXT NOT NULL,
            "VolumeName" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT NOT NULL,
            "FileName" TEXT NOT NULL,
            "SizeBytes" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "Notes" TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_VolumeBackups_BackupId" ON "VolumeBackups" ("BackupId");
        CREATE INDEX IF NOT EXISTS "IX_VolumeBackups_ServerId_VolumeName" ON "VolumeBackups" ("ServerId", "VolumeName");
        CREATE INDEX IF NOT EXISTS "IX_VolumeBackups_CreatedAt" ON "VolumeBackups" ("CreatedAt");

        CREATE TABLE IF NOT EXISTS "ScheduledTasks" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "TaskId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "CronExpression" TEXT NOT NULL,
            "TaskType" INTEGER NOT NULL,
            "TargetId" TEXT,
            "TargetName" TEXT,
            "ServerId" TEXT,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "LastRun" TEXT,
            "NextRun" TEXT,
            "LastResult" TEXT,
            "LastSuccess" INTEGER NOT NULL DEFAULT 0,
            "Config" TEXT,
            "CreatedBy" TEXT NOT NULL DEFAULT '',
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ScheduledTasks_TaskId" ON "ScheduledTasks" ("TaskId");
        CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_Enabled" ON "ScheduledTasks" ("Enabled");
        CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_NextRun" ON "ScheduledTasks" ("NextRun");

        CREATE TABLE IF NOT EXISTS "TaskRunHistory" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "TaskId" TEXT NOT NULL,
            "TaskName" TEXT NOT NULL,
            "TaskType" INTEGER NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT,
            "Success" INTEGER NOT NULL,
            "Output" TEXT,
            "Error" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_TaskRunHistory_TaskId" ON "TaskRunHistory" ("TaskId");
        CREATE INDEX IF NOT EXISTS "IX_TaskRunHistory_StartedAt" ON "TaskRunHistory" ("StartedAt");

        CREATE TABLE IF NOT EXISTS "LogAlertRules" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "RuleId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "ContainerId" TEXT,
            "ContainerName" TEXT,
            "Pattern" TEXT NOT NULL,
            "IsRegex" INTEGER NOT NULL DEFAULT 0,
            "Severity" TEXT NOT NULL DEFAULT 'warning',
            "NotifyMatrix" INTEGER NOT NULL DEFAULT 1,
            "NotifyMattermost" INTEGER NOT NULL DEFAULT 1,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "CooldownMinutes" INTEGER NOT NULL DEFAULT 10,
            "LastTriggered" TEXT,
            "TriggerCount" INTEGER NOT NULL DEFAULT 0,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LogAlertRules_RuleId" ON "LogAlertRules" ("RuleId");
        CREATE INDEX IF NOT EXISTS "IX_LogAlertRules_Enabled" ON "LogAlertRules" ("Enabled");

        CREATE TABLE IF NOT EXISTS "UpdatePolicies" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT,
            "AutoUpdate" INTEGER NOT NULL DEFAULT 0,
            "AutoRollback" INTEGER NOT NULL DEFAULT 1,
            "NotifyBeforeUpdate" INTEGER NOT NULL DEFAULT 1,
            "CheckIntervalMinutes" INTEGER NOT NULL DEFAULT 60,
            "LastChecked" TEXT,
            "LastUpdated" TEXT,
            "LastUpdateResult" TEXT
        );

        CREATE TABLE IF NOT EXISTS "UpdateHistory" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT,
            "OldImageDigest" TEXT NOT NULL,
            "NewImageDigest" TEXT NOT NULL,
            "Image" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT,
            "Success" INTEGER NOT NULL,
            "RolledBack" INTEGER NOT NULL DEFAULT 0,
            "Error" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_UpdateHistory_StartedAt" ON "UpdateHistory" ("StartedAt");

        CREATE TABLE IF NOT EXISTS "Webhooks" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "WebhookId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Secret" TEXT NOT NULL,
            "TargetType" TEXT NOT NULL DEFAULT 'container',
            "TargetId" TEXT NOT NULL,
            "Action" TEXT NOT NULL DEFAULT 'restart',
            "ServerId" TEXT,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "CreatedAt" TEXT NOT NULL,
            "LastTriggered" TEXT,
            "TriggerCount" INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Webhooks_WebhookId" ON "Webhooks" ("WebhookId");

        CREATE TABLE IF NOT EXISTS "WebhookLogs" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "WebhookId" TEXT NOT NULL,
            "WebhookName" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "Success" INTEGER NOT NULL,
            "Output" TEXT,
            "Error" TEXT,
            "SourceIp" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_WebhookLogs_Timestamp" ON "WebhookLogs" ("Timestamp");
        """;
    await cmd.ExecuteNonQueryAsync();
}

app.Run();
