using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Whiskers.Configuration;

namespace Whiskers.Startup;

/// <summary>The authentication wiring — cookie session + optional Google/OIDC providers, or full bypass for
/// trusted LAN-only deployments. Isolated from the rest of the composition root because it is the
/// security-sensitive protected layer (docs/ARCHITECTURE.md, ADR-0002): the provider events and the cookie policy were
/// moved here verbatim from Program.cs. One deliberate change since (user-approved 2026-11-03, R1-26): the cookie's
/// per-request check is no longer the fail-open whitelist re-check but the fail-closed session check
/// (<c>SessionCookieValidation</c> → <c>ISessionValidator</c>). R1-27 (contract 0003, approved 2026-11-04): one claim
/// action per provider maps <c>email_verified</c>; the gate's body decides through <c>IFederatedAdmission</c> on the
/// provider's subject. The middleware ORDER that enforces auth lives in
/// <see cref="WhiskersPipelineExtensions"/> and is unchanged.</summary>
public static class WhiskersAuthenticationExtensions
{
    public static void AddWhiskersAuthentication(this WebApplicationBuilder builder)
    {
        // Authentication — cookie session + optional federated providers (Google and/or generic OIDC),
        // or full bypass for trusted LAN-only deployments.
        var authDisabled = builder.Configuration.GetValue<bool>("Auth:Disabled");
        var googleAuthSection = builder.Configuration.GetSection(GoogleAuthSettings.SectionName);
        var googleClientId = googleAuthSection["ClientId"];
        // Reverse-proxy subpath — used by the shared whitelist gate's unauthorized redirect.
        var configuredPathBase = builder.Configuration["PathBase"] ?? "";

        builder.Services.Configure<OidcSettings>(builder.Configuration.GetSection(OidcSettings.SectionName));
        var oidcSection = builder.Configuration.GetSection(OidcSettings.SectionName);
        var oidcEnabled = oidcSection.GetValue<bool>("Enabled") && !string.IsNullOrWhiteSpace(oidcSection["Authority"]);

        // Shared gate: after a provider authenticates the user, enforce the email whitelist before issuing
        // the local cookie. Used by both Google and OIDC; both outcomes and a failed round trip are audited (R1-22).
        Task WhitelistGate(Microsoft.AspNetCore.Authentication.TicketReceivedContext context) =>
            Whiskers.Services.Auth.FederatedSignIn.WhitelistGateAsync(context, configuredPathBase);
        Task RecordRemoteFailure(Microsoft.AspNetCore.Authentication.RemoteFailureContext context) =>
            Whiskers.Services.Auth.FederatedSignIn.RecordRemoteFailureAsync(context);

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

                    // R1-26 (INV-03/09, approved 2026-11-03): every request re-checks the session — synthetic
                    // identities (TA-11), a deleted or changed local account and its security stamp, the whitelist —
                    // and FAILS CLOSED: an error rejects the cookie. See SessionCookieValidation / ISessionValidator.
                    options.Events.OnValidatePrincipal = Whiskers.Services.Auth.SessionCookieValidation.ValidateAsync;
                });

            if (!string.IsNullOrWhiteSpace(googleClientId))
            {
                authBuilder.AddGoogle(options =>
                {
                    options.ClientId = googleClientId!;
                    options.ClientSecret = googleAuthSection["ClientSecret"] ?? "";
                    // R1-27 (contract 0003, approved): the admission binds a new subject only on a verified address.
                    options.ClaimActions.MapJsonKey(Whiskers.Services.Auth.AuthConstants.EmailVerifiedClaim, "email_verified");
                    options.Events.OnTicketReceived = WhitelistGate;
                    options.Events.OnRemoteFailure = RecordRemoteFailure;
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
                    // R1-27 (contract 0003, approved): email_verified from userinfo; it vouches for "email" only.
                    options.ClaimActions.MapUniqueJsonKey(Whiskers.Services.Auth.AuthConstants.EmailVerifiedClaim, "email_verified");

                    options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                    {
                        OnTicketReceived = WhitelistGate,
                        OnRemoteFailure = RecordRemoteFailure
                    };
                });
            }
        }

        // Local username/password login (F1) — ADDITIVE. AddIdentityCore registers UserManager<AppUser> + the
        // EF user store + password hashing ONLY; it does NOT add an authentication/cookie scheme and does NOT
        // override the default scheme, so the cookie session + Google/OIDC wiring above remain the sole auth
        // setup. NEVER AddIdentity/AddDefaultIdentity here (those register competing cookie schemes). No
        // AddRoles → a UserOnlyStore, no RoleManager, no parallel role system (roles stay in roles.json).
        builder.Services.AddIdentityCore<Whiskers.Services.Persistence.AppUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.User.RequireUniqueEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;
        })
        .AddEntityFrameworkStores<Whiskers.Services.Persistence.WhiskersIdentityDbContext>();

        // Brute-force brake in front of the per-account lockout: per client IP, a handful of login POSTs per
        // minute. The account lockout alone would let an attacker cycle through many accounts (audit TA-03).
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LocalLoginTiming.RateLimitPolicy, ctx =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        // KRIT-3 step 2 (fail-closed by default; user approval 2026-07-11): every endpoint WITHOUT
        // explicit authorization metadata now requires an authenticated user. Anonymous surfaces are
        // explicit opt-outs: /healthz+/readyz, /set-culture, the login endpoints, /logout,
        // /api/webhooks (HMAC-authenticated), /metrics (scrape-token-gated), static assets, and the
        // Razor-components/circuit endpoints (page-level auth stays with AuthorizeRouteView +
        // the default [Authorize] in _Imports.razor; Login/Setup/Error opt out via [AllowAnonymous]).
        // /mcp keeps its own explicit RequireAuthorization policy (bearer-key principal).
        // Auth:Disabled mode is unaffected: the synthetic-principal middleware authenticates every
        // request before authorization runs.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        builder.Services.AddCascadingAuthenticationState();
    }
}
