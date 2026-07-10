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
/// ORDER is security-critical (CLAUDE.md Off-Limits): forwarded headers first, then the fixed
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
                // Generic failure for both "no such user" and "wrong password" — no account enumeration.
                if (user is null || !await users.CheckPasswordAsync(user, password ?? ""))
                    return Results.Redirect(configuredPathBase + "/login?error=invalid");

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
            });
        }

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
        app.MapRazorComponents<Whiskers.Components.App>()
            .AddInteractiveServerRenderMode();
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
        }
    }
}
