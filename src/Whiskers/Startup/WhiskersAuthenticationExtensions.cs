using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Whiskers.Configuration;

namespace Whiskers.Startup;

/// <summary>The authentication wiring — cookie session + optional Google/OIDC providers, or full bypass for
/// trusted LAN-only deployments. Isolated from the rest of the composition root because it is the
/// security-sensitive Off-Limits zone (CLAUDE.md, ADR-0002): the fail-open whitelist re-check, the provider
/// events, and the cookie policy are moved here <b>verbatim</b> from Program.cs — same branches, same order,
/// same semantics. Only the location changed; the middleware ORDER that enforces auth lives in
/// <see cref="WhiskersPipelineExtensions"/> and is likewise unchanged.</summary>
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
    }
}
