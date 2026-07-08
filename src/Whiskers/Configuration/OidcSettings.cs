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

    /// <summary>Allow http metadata — only for local/dev IdPs. Keep true in production.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
