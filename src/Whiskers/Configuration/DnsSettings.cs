namespace Whiskers.Configuration;

/// <summary>Global DNS-provider access (one account, many zones) — unlike cloud credentials, which are
/// per-server. Persisted via <c>IAppSettingsStore</c> under the <c>"Dns"</c> section of app-settings.json and
/// editable in <i>Settings → DNS</i>. The token is a secret: never log it, never return it from a tool. Since R1-29 it is
/// kept in the vault (<see cref="ApiTokenRef"/>); app-settings.json never holds it.</summary>
public class DnsSettings
{
    public const string SectionName = "Dns";

    /// <summary>Provider id. Only <c>"infomaniak"</c> is implemented today.</summary>
    public string Provider { get; set; } = "infomaniak";

    /// <summary>R1-29: the vault key that holds the provider API token (Infomaniak: a token with the <c>domain</c>
    /// scope). A new key per change; the token itself is never written to app-settings.json.</summary>
    public string? ApiTokenRef { get; set; }

    /// <summary>A plaintext token: legacy (before R1-29, moved into the vault at the first start with a usable vault)
    /// or set by the environment (<c>Dns__ApiToken</c>). Used only when <see cref="ApiTokenRef"/> is not set; the
    /// settings store refuses to write one.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Zones the tools may touch. Empty = every zone the token can reach. A non-empty list is the
    /// blast-radius fence: a typo in the zone argument then fails instead of editing a neighbouring domain.</summary>
    public List<string> AllowedZones { get; set; } = new();

    /// <summary>TTL applied when a caller passes none (seconds).</summary>
    public int DefaultTtl { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiTokenRef) || !string.IsNullOrWhiteSpace(ApiToken);
}
