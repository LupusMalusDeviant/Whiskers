using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Configuration;
using Whiskers.Utils;
using Whiskers.Hubs;
using Whiskers.Services.Metrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Startup;

/// <summary>The HTTP request pipeline + startup warm-up, moved <b>verbatim</b> from Program.cs. The middleware
/// ORDER is security-critical (protected layer, docs/ARCHITECTURE.md): forwarded headers first, then the fixed
/// <c>Antiforgery → Authentication → [LAN bypass] → McpBearer → Authorization</c> chain. This method keeps
/// every <c>Use*</c>/<c>Map*</c> call in the exact same order as before — nothing is reordered, only relocated
/// (the McpCallLog middleware still sits between the endpoint maps exactly where it did).</summary>
public static class WhiskersPipelineExtensions
{
    public static void ConfigureWhiskersHttpPipeline(this WebApplication app)
    {
        var configuredPathBase = app.Configuration["PathBase"] ?? "";
        var authDisabled = app.Configuration.GetValue<bool>("Auth:Disabled");

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

        // W1 first-run setup redirect (ADDITIVE — does NOT touch the auth chain below). Placed BEFORE
        // UseAuthentication so the decision is auth-independent, and registered only when auth is real
        // (Auth:Disabled is the LAN escape hatch — no wizard). Until an admin exists, every top-level HTML
        // navigation is funneled to /setup; infra endpoints + sub-resources (non-HTML Accept) pass through.
        // Once complete, /setup is a dead route (→ /). 302 (not 301 — a 301 would be cached past setup).
        if (!authDisabled)
        {
            app.Use(async (ctx, next) =>
            {
                var setup = ctx.RequestServices.GetRequiredService<Whiskers.Services.Setup.ISetupStateService>();
                var path = ctx.Request.Path;
                if (setup.IsSetupComplete)
                {
                    if (path.StartsWithSegments("/setup"))
                    {
                        ctx.Response.Redirect(configuredPathBase + "/");
                        return;
                    }
                }
                else if (!path.StartsWithSegments("/setup")
                         && !SetupRedirectPaths.IsExempt(path)
                         && SetupRedirectPaths.IsHtmlNavigation(ctx.Request))
                {
                    ctx.Response.Redirect(configuredPathBase + "/setup");
                    return;
                }
                await next();
            });
        }

        // F3 maintenance gate (ADDITIVE — does NOT touch the auth chain). While a restore is staged and the
        // process is about to restart, return 503 for top-level HTML navigations so the admin sees a clear
        // "restarting" page instead of errors against half-swapped state; infra endpoints + sub-resources pass
        // through. Runs in BOTH auth modes (a restore can happen under Auth:Disabled too). Same seam as the
        // setup redirect: after UseAntiforgery, before UseAuthentication.
        app.Use(async (ctx, next) =>
        {
            var maintenance = ctx.RequestServices.GetRequiredService<Whiskers.Services.Maintenance.IMaintenanceStateService>();
            if (maintenance.IsMaintenance
                && !MaintenancePaths.IsExempt(ctx.Request.Path)
                && SetupRedirectPaths.IsHtmlNavigation(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "30";
                ctx.Response.ContentType = "text/html; charset=utf-8";
                var reason = System.Net.WebUtility.HtmlEncode(maintenance.Reason ?? "Wartungsmodus");
                await ctx.Response.WriteAsync(
                    "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\">" +
                    "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                    "<meta http-equiv=\"refresh\" content=\"15\"><title>Whiskers — Wartung</title></head>" +
                    "<body style=\"font-family:system-ui,sans-serif;background:#111;color:#eee;display:flex;" +
                    "align-items:center;justify-content:center;height:100vh;margin:0;text-align:center\">" +
                    "<div><h1 style=\"font-weight:600\">🐾 Whiskers wird gewartet</h1>" +
                    $"<p style=\"color:#aaa\">{reason}</p>" +
                    "<p style=\"color:#777;font-size:.9em\">Diese Seite lädt automatisch neu…</p></div></body></html>");
                return;
            }
            await next();
        });

        app.UseAuthentication();

        // Auth bypass for trusted LAN deployments — inject a synthetic authenticated
        // principal so [Authorize]-protected pages render without a login flow.
        if (authDisabled)
        {
            var access = Whiskers.Services.Auth.AuthDisabledAccessPolicy.FromConfiguration(app.Configuration);
            app.Use(async (ctx, next) =>
            {
                // Every request below this point is an Admin — refuse anything that is not plainly from the trusted
                // network (public client, DNS-rebinding Host, cross-site Origin). See AuthDisabledAccessPolicy.
                var refused = access.Check(ctx.Connection.RemoteIpAddress, ctx.Request.Host.Value, ctx.Request.Headers.Origin);
                if (refused is not null)
                {
                    ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Whiskers.AuthDisabled")
                        .LogWarning("Auth:Disabled request refused ({Reason}) from {Remote}", refused, ctx.Connection.RemoteIpAddress);
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsync($"Forbidden: Auth:Disabled only serves the trusted network ({refused}).");
                    return;
                }
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
        // Endpoint-scoped limits only (the login POST); nothing else is throttled.
        app.UseRateLimiter();

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
        }).AllowAnonymous();

        app.MapGet("/login-oidc", async (HttpContext ctx) =>
        {
            await ctx.ChallengeAsync("oidc", new AuthenticationProperties
            {
                RedirectUri = configuredPathBase + "/"
            });
        }).AllowAnonymous();

        // Local username/password login (F1). Only when auth is real. Validates against the Identity user
        // store, applies the SAME whitelist gate as Google/OIDC, then issues the EXISTING cookie carrying a
        // ClaimTypes.Email claim so the role/whitelist system resolves the user identically to a federated one.
        // [FromForm] marks the endpoint antiforgery-required; the existing UseAntiforgery() validates the token.
        if (!authDisabled)
        {
            app.MapPost("/login-local", async (
                HttpContext ctx,
                [Microsoft.AspNetCore.Mvc.FromForm] string email,
                [Microsoft.AspNetCore.Mvc.FromForm] string password,
                UserManager<AppUser> users,
                Whiskers.Services.Auth.IWhitelistService whitelist) =>
            {
                var user = await users.FindByEmailAsync(email ?? "");
                // Generic failure for "no such user", "wrong password" and "locked out" — no account enumeration.
                if (user is null)
                {
                    // Spend the same hashing work as a real check so response time doesn't reveal unknown accounts.
                    LocalLoginTiming.BurnPasswordCheck(users, password);
                    return Results.Redirect(configuredPathBase + "/login?error=invalid");
                }
                // CheckPasswordAsync neither honours nor counts lockout (audit TA-03), so do both explicitly:
                // MaxFailedAccessAttempts (5) wrong passwords lock the account for DefaultLockoutTimeSpan.
                if (await users.IsLockedOutAsync(user))
                    return Results.Redirect(configuredPathBase + "/login?error=invalid");
                if (!await users.CheckPasswordAsync(user, password ?? ""))
                {
                    await users.AccessFailedAsync(user);
                    return Results.Redirect(configuredPathBase + "/login?error=invalid");
                }
                await users.ResetAccessFailedCountAsync(user);

                if (!whitelist.IsEmailAllowed(user.Email))
                    return Results.Redirect(configuredPathBase + "/login?error=unauthorized");

                // Build the principal MANUALLY so it carries ClaimTypes.Email — the default claims factory does
                // not, and every role consumer keys on the email claim. authenticationType is non-empty (so
                // IsAuthenticated is true) and deliberately NOT "AuthDisabled"/"AgentSynthetic".
                var identity = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Email, user.Email!),
                    new Claim(ClaimTypes.Name, user.Email!),
                    new Claim(ClaimTypes.NameIdentifier, user.Id),
                }, authenticationType: "LocalPassword");
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
                return Results.Redirect(configuredPathBase + "/");
            }).AllowAnonymous().RequireRateLimiting(LocalLoginTiming.RateLimitPolicy);
        }

        app.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect(configuredPathBase + "/login");
        }).AllowAnonymous();

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
                if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) // NIED-1: scheme is case-insensitive
                {
                    var key = authHeader["Bearer ".Length..];
                    // The permission service is the only key source. The pre-0.15 flat `api-keys.json`
                    // fallback is gone, so a key without a scope can no longer pass this gate.
                    return permService?.ValidateKey(key) != null;
                }
                // Also allow authenticated web users
                return context.User.Identity?.IsAuthenticated == true;
            }));

        // F3 self-backup download. Streams a self-backup archive to an ADMIN web user (or the trusted-LAN
        // AuthDisabled principal). The archive is a secret-bearing artifact — even when VAULT_KEY-encrypted it
        // is restricted. The id is validated (SafeName) and resolved strictly within backups/self by the
        // service, so this endpoint cannot be steered outside the backups directory.
        app.MapGet("/backups/self/{id}/download", (string id, HttpContext ctx) =>
        {
            var backups = ctx.RequestServices.GetRequiredService<Whiskers.Services.Backup.IBackupService>();
            var path = backups.ResolveArchivePath(id);
            if (path is null) return Results.NotFound();
            return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
        }).RequireAuthorization(policy => policy.RequireAssertion(context =>
        {
            var httpContext = context.Resource as HttpContext;
            if (httpContext is null) return false;
            if (httpContext.User.Identity?.AuthenticationType == Whiskers.Services.Auth.AuthConstants.AuthDisabledScheme)
                return true;   // trusted-LAN bypass = admin, consistent with the rest of the app
            var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value;
            var roles = httpContext.RequestServices.GetService<Whiskers.Services.Auth.IRoleService>();
            return roles?.GetRole(email) >= Whiskers.Models.AppRole.Admin;
        }));

        // Webhook API endpoint (no auth — uses HMAC signature validation). R1-10: bounded, deduplicated, queued.
        app.MapPost("/api/webhooks/{webhookId}", (string webhookId, HttpContext ctx) =>
            Whiskers.Services.Webhooks.WebhookEndpoint.HandleAsync(webhookId, ctx)).AllowAnonymous();

        // Prometheus metrics endpoint. Gated by a static scrape token (Metrics:ScrapeToken) because the
        // payload is the full multi-server container inventory. With no token configured the endpoint stays
        // disabled (opt-in) rather than served openly — the safe default for the hardened host-network profile.
        var metricsScrapeToken = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<MetricsSettings>>().Value.ScrapeToken;
        app.MapGet("/metrics", async (HttpContext ctx) =>
        {
            // The Prometheus text format is NOT localised: every value needs a dot decimal separator. This
            // endpoint sits behind UseRequestLocalization, which supports "de", so a scraper (or a browser)
            // sending Accept-Language: de would otherwise render "0,120" — and Prometheus rejects the whole
            // scrape on the first unparsable line. The monitoring would go dark because of a request header.
            // Pinned for this request only; the finally puts the request's own culture back.
            var callerCulture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
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

            // Whiskers about ITSELF, before anything that needs the Docker API (Plan-0003 WP3). Deliberately
            // first and outside the try: these are process-local counters that cannot fail, and they are the
            // numbers you need precisely when the fleet is not answering. Exporting the whole container
            // inventory while saying nothing about the exporter is how a monitor stays blind to itself —
            // the log monitor wrote "timed out" every cycle for six days and no series carried it.
            AppendSelfMetrics(sb, ctx.RequestServices);

            try
            {
                var containers = await docker.ListAllContainersAsync(all: true);

                // Metric names keep their pre-rebrand `serverwatch_` prefix so existing Prometheus
                // dashboards and alert rules keep working; renaming them is a breaking change for scrapers.
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

            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = callerCulture;
            }
        }).AllowAnonymous(); // gated by the scrape token inside, not by user auth

        // KRIT-3 step 2: static assets stay anonymous (the login page needs its CSS/JS). The
        // component PAGE endpoints are NOT blanket-exempted — each page carries [Authorize] by
        // default (_Imports.razor) and the anonymous pages (Login/Setup/Error) opt out per
        // attribute, so the fallback policy + endpoint metadata enforce page auth at the HTTP
        // level too. Only the Blazor FRAMEWORK endpoints (/_blazor circuit, initializers,
        // disconnect) get a targeted anonymous opt-out — without it the fallback policy 302s the
        // circuit and the anonymous pages (Login, Setup wizard) lose interactivity; the circuit
        // itself carries no content, page auth still gates everything rendered through it.
        app.MapStaticAssets().AllowAnonymous();
        app.MapRazorComponents<Whiskers.Components.App>()
            .AddInteractiveServerRenderMode()
            .Add(endpointBuilder =>
            {
                if (endpointBuilder is RouteEndpointBuilder reb
                    && reb.RoutePattern.RawText is { } raw
                    && raw.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase))
                {
                    endpointBuilder.Metadata.Add(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
                }
            });
    }

    /// <summary>Startup warm-up run after Build(): each IInitializable in ascending Order, then the metrics
    /// database migration/baseline. Moved verbatim from Program.cs.</summary>
    public static async Task RunWhiskersStartupAsync(this WebApplication app)
    {
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

        // R1-14: the approvals live in the metrics database, so they load only once its schema is current. Pending
        // agent approvals are cancelled here (their sessions are gone); approve-then-retry approvals and unused
        // grants carry on.
        await app.Services.GetRequiredService<Whiskers.Services.Approvals.IApprovalStore>().LoadAsync(CancellationToken.None);

        // Local-auth (F1): migrate the Identity schema in its OWN __IdentityMigrationsHistory table (kept off
        // the metrics legacy-baseline path), then seed the unattended admin if configured. Brand-new tables →
        // a straight MigrateAsync (Npgsql retry handles transient connects). Skip the seed when auth is
        // bypassed (Auth:Disabled has no login).
        using (var scope = app.Services.CreateScope())
        {
            var idDb = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            await idDb.Database.MigrateAsync();

            if (!app.Configuration.GetValue<bool>("Auth:Disabled"))
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LocalAdminSeeder");
                await Whiskers.Services.Auth.LocalAdminSeeder.SeedAsync(users, app.Configuration, seedLogger);
            }

            // Record the local accounts that exist now, so one deleted later is noticed by automation ownership
            // with federated sign-in configured too (R1-04).
            app.Services.GetRequiredService<Whiskers.Services.Mutations.IOwnerAccounts>().RememberLocalAccounts();
        }

        // Module warm-ups (IWhiskersModule.InitializeAsync). Declared since RoadToSAP Phase 0 but never
        // invoked until F11 needed it — wired here, AFTER the database migration, so a module's init can
        // safely touch its tables (first consumer: the Webhooks module's secret-mandatory upgrade path).
        // DiscoverEnabled is deterministic, so this list matches the one used for ConfigureServices.
        foreach (var module in Whiskers.Modules.ModuleCatalog.DiscoverEnabled(app.Configuration))
            await module.InitializeAsync(app.Services, CancellationToken.None);
    }

    /// <summary>The <c>whiskers_self_*</c> series: what Whiskers is doing to the fleet and how its own loops
    /// are faring (Plan-0003 WP3). Reads process-local counters only — no Docker call, no database round trip,
    /// so this section is still there when everything else is broken.
    ///
    /// <para>Label cardinality is bounded by servers and loops, never by containers: a container label across
    /// a 200-container fleet would multiply the series into the thousands and fill the time-series database —
    /// a monitoring outage caused by monitoring.</para></summary>
    private static void AppendSelfMetrics(System.Text.StringBuilder sb, IServiceProvider services)
    {
        static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var loops = services.GetService<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>();
        var budget = services.GetService<Whiskers.Services.Docker.Budget.IServerBudget>();
        var circuit = services.GetService<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>();
        var exclusions = services.GetService<Whiskers.Services.LogMonitor.Hygiene.ILogScanExclusions>();
        var suspensions = services.GetService<Whiskers.Services.Observability.ILoopSuspensionService>();
        var now = DateTime.UtcNow;

        if (loops is not null)
        {
            // Plan-0003 WP6.3 — cardinality, tracked as a number rather than assumed. This is how you notice
            // between releases that a label was added which multiplies the series count: the failure mode of a
            // time-series database is not an error, it is silently becoming too expensive to keep.
            var seriesCount = loops.Loops().Count * 6 + loops.Counters().Sum(c => c.Value.Count);
            sb.AppendLine("# HELP whiskers_self_series_total How many whiskers_self_* series this endpoint produces. Compare between releases: growth beyond server and loop count means a label was added that multiplies cardinality.");
            sb.AppendLine("# TYPE whiskers_self_series_total gauge");
            sb.AppendLine($"whiskers_self_series_total {seriesCount}");
        }

        var hostLoad = services.GetService<Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator>();
        if (hostLoad is not null)
        {
            // Plan-0004 WP5.4. The value is less interesting than its shape over time: an open-findings count
            // that only ever climbs means the closing path is broken, and a monitor whose alerts never close
            // stops being read long before anybody works out why. The age of the oldest one is the giveaway.
            var open = hostLoad.OpenFindings();
            sb.AppendLine("# HELP whiskers_host_findings_open Host findings raised and not yet closed. A count that only ever grows means the all-clear path is broken, not that the problems are patient.");
            sb.AppendLine("# TYPE whiskers_host_findings_open gauge");
            foreach (var group in open.GroupBy(f => (f.ServerId, f.Kind)))
                sb.AppendLine($"whiskers_host_findings_open{{server=\"{Esc(group.Key.ServerId)}\",kind=\"{Esc(group.Key.Kind)}\"}} {group.Count()}");

            sb.AppendLine("# HELP whiskers_host_finding_oldest_age_seconds Age of the oldest unclosed host finding. Past a week it is evidence about the closing path, not about the server.");
            sb.AppendLine("# TYPE whiskers_host_finding_oldest_age_seconds gauge");
            sb.AppendLine($"whiskers_host_finding_oldest_age_seconds {(open.Count > 0 ? (now - open[0].SinceUtc).TotalSeconds : 0):F0}");
        }

        var cveStore = services.GetService<Whiskers.Services.Cve.ICveFindingsStore>();
        var cveSettings = services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<CveMonitorSettings>>();
        if (cveStore is not null && cveSettings is not null)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, cveSettings.CurrentValue.CheckIntervalHours));
            var cve = Whiskers.Services.Cve.CveMetrics.Build(cveStore.GetAll(), interval, now);

            sb.AppendLine("# HELP whiskers_cve_findings Open CVE findings per server and severity. The count of instances, not of distinct vulnerabilities — see whiskers_cve_distinct_ids.");
            sb.AppendLine("# TYPE whiskers_cve_findings gauge");
            foreach (var server in cve.PerServer)
                foreach (var (severity, count) in server.BySeverity)
                    sb.AppendLine($"whiskers_cve_findings{{server=\"{Esc(server.ServerId)}\",severity=\"{severity}\"}} {count}");

            sb.AppendLine("# HELP whiskers_cve_distinct_ids Distinct CVE identifiers across the fleet. The findings count says how much work the display has; this says how many actual problems there are.");
            sb.AppendLine("# TYPE whiskers_cve_distinct_ids gauge");
            sb.AppendLine($"whiskers_cve_distinct_ids {cve.DistinctCveIds}");

            // THE series this section exists for. A failed scan deliberately keeps the previous results rather
            // than reporting a false all-clear, so a target whose scanner broke months ago looks exactly like
            // one scanned this morning — same findings, still plausible, simply never changing. The only thing
            // that tells them apart is how old the data is, and until now nothing was watching it.
            sb.AppendLine("# HELP whiskers_cve_data_age_seconds Age of the least recently scanned target on this server. A failed scan keeps the old findings, so stale data looks exactly like a clean bill of health — this is the number that tells them apart.");
            sb.AppendLine("# TYPE whiskers_cve_data_age_seconds gauge");
            foreach (var server in cve.PerServer)
                sb.AppendLine($"whiskers_cve_data_age_seconds{{server=\"{Esc(server.ServerId)}\"}} {server.OldestDataAge.TotalSeconds:F0}");

            // Found on 2026-08-28. A target whose scan fails and that has no earlier result used to be stored
            // nowhere at all — absent from every list, which reads exactly like a target with no findings.
            // Staleness cannot see this: what is not there cannot age. This can.
            sb.AppendLine("# HELP whiskers_cve_scan_failures Targets whose last scan failed. Their zero findings mean \"we do not know\", not \"nothing found\" — alert on this above zero.");
            sb.AppendLine("# TYPE whiskers_cve_scan_failures gauge");
            foreach (var server in cve.PerServer)
                sb.AppendLine($"whiskers_cve_scan_failures{{server=\"{Esc(server.ServerId)}\"}} {server.FailedTargets}");

            sb.AppendLine("# HELP whiskers_cve_stale_targets Targets whose data is older than two scan intervals — a scanner that has stopped, not a blip. Alert on this being above zero.");
            sb.AppendLine("# TYPE whiskers_cve_stale_targets gauge");
            foreach (var server in cve.PerServer)
                sb.AppendLine($"whiskers_cve_stale_targets{{server=\"{Esc(server.ServerId)}\"}} {server.StaleTargets}");
        }

        if (exclusions is not null)
        {
            // Plan-0007 WP2.2. The number to watch is not its value but its MOVEMENT: exclusions that appear
            // without a configuration change mean the access-path detection has grown too greedy, and a
            // wrongly excluded container looks exactly like one with nothing to report.
            sb.AppendLine("# HELP whiskers_log_scan_exclusions Containers the log scan steps over on this server, by reason. Growth without a configuration change means the detection is too greedy.");
            sb.AppendLine("# TYPE whiskers_log_scan_exclusions gauge");
            foreach (var group in exclusions.Current().GroupBy(e => (e.ServerId, e.Reason)))
                sb.AppendLine($"whiskers_log_scan_exclusions{{server=\"{Esc(group.Key.ServerId)}\",reason=\"{Esc(group.Key.Reason)}\"}} {group.Count()}");
        }

        if (suspensions is not null)
        {
            // Plan-0005. A paused server reports nothing, which reads like a quiet one — so the pause itself
            // has to be a number somebody can alert on.
            sb.AppendLine("# HELP whiskers_loops_paused Whether background checks for this server are paused (1) or running (0). A paused server produces no findings; that is not the same as having none.");
            sb.AppendLine("# TYPE whiskers_loops_paused gauge");
            foreach (var paused in suspensions.Current())
                sb.AppendLine($"whiskers_loops_paused{{server=\"{Esc(paused.ServerId)}\",automatic=\"{(paused.Automatic ? "true" : "false")}\"}} 1");
        }

        if (loops is not null)
        {
            sb.AppendLine("# HELP whiskers_self_loop_last_success_age_seconds Seconds since this loop last completed a cycle for this server. THE number that reveals a loop which has stopped: failures are only counted while something still happens, a stalled loop produces nothing at all.");
            sb.AppendLine("# TYPE whiskers_self_loop_last_success_age_seconds gauge");
            foreach (var l in loops.Loops())
            {
                var age = l.LastSuccess is { } t ? (now - t).TotalSeconds : -1;
                sb.AppendLine($"whiskers_self_loop_last_success_age_seconds{{loop=\"{Esc(l.Loop)}\",server=\"{Esc(l.ServerId)}\"}} {age:F0}");
            }

            sb.AppendLine("# HELP whiskers_self_loop_cycle_seconds Duration of this loop's last cycle for this server");
            sb.AppendLine("# TYPE whiskers_self_loop_cycle_seconds gauge");
            foreach (var l in loops.Loops())
                sb.AppendLine($"whiskers_self_loop_cycle_seconds{{loop=\"{Esc(l.Loop)}\",server=\"{Esc(l.ServerId)}\"}} {l.LastDuration.TotalSeconds:F3}");

            sb.AppendLine("# HELP whiskers_self_loop_cycles_total Cycles run, failed and deliberately skipped");
            sb.AppendLine("# TYPE whiskers_self_loop_cycles_total counter");
            foreach (var l in loops.Loops())
            {
                var labels = $"loop=\"{Esc(l.Loop)}\",server=\"{Esc(l.ServerId)}\"";
                sb.AppendLine($"whiskers_self_loop_cycles_total{{{labels},result=\"ok\"}} {l.Cycles - l.Failures}");
                sb.AppendLine($"whiskers_self_loop_cycles_total{{{labels},result=\"failed\"}} {l.Failures}");
                // Skips are exported too: a server a loop never looks at must not simply be absent, or
                // "not monitored here" reads exactly like "nothing to report".
                sb.AppendLine($"whiskers_self_loop_cycles_total{{{labels},result=\"skipped\"}} {l.Skips}");
            }

            foreach (var (name, perServer) in loops.Counters())
            {
                sb.AppendLine($"# TYPE whiskers_self_{name}_total counter");
                foreach (var (server, value) in perServer)
                    sb.AppendLine($"whiskers_self_{name}_total{{server=\"{Esc(server)}\"}} {value}");
            }
        }

        if (budget is not null)
        {
            sb.AppendLine("# HELP whiskers_self_calls_in_flight Docker calls Whiskers currently has open against this server");
            sb.AppendLine("# TYPE whiskers_self_calls_in_flight gauge");
            sb.AppendLine("# HELP whiskers_self_call_wait_seconds_max Longest a call has waited for a budget slot. A rising value means the budget is the bottleneck, not the server.");
            sb.AppendLine("# TYPE whiskers_self_call_wait_seconds_max gauge");
            foreach (var b in budget.SnapshotAll())
            {
                var server = $"server=\"{Esc(b.ServerId)}\"";
                sb.AppendLine($"whiskers_self_calls_in_flight{{{server},lane=\"background\"}} {b.BackgroundInFlight}");
                sb.AppendLine($"whiskers_self_calls_in_flight{{{server},lane=\"interactive\"}} {b.InteractiveInFlight}");
                sb.AppendLine($"whiskers_self_call_wait_seconds_max{{{server}}} {b.MaxWaitMilliseconds / 1000.0:F3}");
                sb.AppendLine($"whiskers_self_calls_total{{{server}}} {b.Started}");
                sb.AppendLine($"whiskers_self_duplicate_calls_discarded_total{{{server}}} {b.DiscardedDuplicates}");
            }
        }

        if (circuit is not null)
        {
            sb.AppendLine("# HELP whiskers_self_circuit_open 1 while Whiskers has stopped calling this server. Whiskers is NOT checking that host meanwhile — this is not the same as the host being fine.");
            sb.AppendLine("# TYPE whiskers_self_circuit_open gauge");
            foreach (var c in circuit.SnapshotAll())
                sb.AppendLine($"whiskers_self_circuit_open{{server=\"{Esc(c.ServerId)}\"}} {(c.State == Whiskers.Services.Docker.Budget.ServerCircuitState.Closed ? 0 : 1)}");
        }

        sb.AppendLine();
    }
}
