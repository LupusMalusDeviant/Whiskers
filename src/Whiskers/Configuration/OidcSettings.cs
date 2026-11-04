namespace Whiskers.Configuration;

/// <summary>
/// Generic OpenID Connect login (alongside or instead of Google). Compatible with any standard OIDC
/// provider — Authentik, Keycloak, Pocket ID, Authelia (OIDC), Zitadel, etc. Whiskers only consumes
/// the email claim and still applies the existing whitelist + role checks.
/// Set via env vars: Oidc__Enabled, Oidc__Authority, Oidc__ClientId, Oidc__ClientSecret, ...
/// </summary>
public class OidcSettings
{
    public const string SectionName = "Oidc";

    public bool Enabled { get; set; }

    /// <summary>Issuer/authority URL — the provider exposes /.well-known/openid-configuration under it.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Label shown on the login button: "Mit {DisplayName} anmelden".</summary>
    public string DisplayName { get; set; } = "SSO";

    /// <summary>Space-separated scopes. "openid" is required; "email" is needed for the whitelist match.</summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>Which claim carries the user's email (mapped to ClaimTypes.Email for whitelist/roles).</summary>
    public string EmailClaim { get; set; } = "email";

    /// <summary>R1-27 (contract 0003, decision 3 b): whether this provider's <c>email_verified=true</c> binds a new subject
    /// to a configured address at its first sign-in. Off by default: every OIDC user is confirmed once by an Admin or the
    /// host CLI. Set it only when the IdP emits <c>email_verified</c> honestly and users cannot change their own address.
    /// A non-standard <see cref="EmailClaim"/> never counts as verified.</summary>
    public bool TrustEmailVerified { get; set; }

    /// <summary>Allow http metadata — only for local/dev IdPs. Keep true in production.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
