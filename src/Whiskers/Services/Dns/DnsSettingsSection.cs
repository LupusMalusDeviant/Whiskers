using System.Text.Json.Nodes;
using Whiskers.Configuration;

namespace Whiskers.Services.Dns;

/// <summary>
/// The <c>Dns</c> section of app-settings.json as a save writes it (R1-29): the token is only named by its vault key.
/// Reads the stored section as it is in the file (not merged with the environment), so a save keeps what an earlier
/// one decided — including an empty <c>ApiToken</c> that hides a token set by the environment.
/// </summary>
public static class DnsSettingsSection
{
    /// <summary>The vault key the stored section names, or null.</summary>
    public static string? StoredRef(JsonObject? stored) => Field(stored, nameof(DnsSettings.ApiTokenRef)) is { Length: > 0 } r ? r : null;

    /// <summary>A plaintext token still stored in the file (legacy, before R1-29), when the section names no vault key.</summary>
    public static string? PlaintextToken(JsonObject? stored)
        => StoredRef(stored) is null && Field(stored, nameof(DnsSettings.ApiToken)) is { } t && !string.IsNullOrWhiteSpace(t) ? t : null;

    /// <summary>Whether the stored section hides a token set by the environment with an empty <c>ApiToken</c>.</summary>
    public static bool HidesEnvironmentToken(JsonObject? stored)
        => Field(stored, nameof(DnsSettings.ApiToken)) is { } t && string.IsNullOrWhiteSpace(t);

    /// <summary>The section to write: provider, zones, TTL and the vault key — <paramref name="newRef"/> or the stored one.
    /// Never a new plaintext token; an empty one only where the stored section already hid the environment's. A plaintext
    /// token found in the stored section without a new vault key is carried over, so the store refuses the write instead
    /// of the token silently leaving the file without being in the vault.</summary>
    public static JsonObject Next(JsonObject? stored, string provider, string? newRef, IEnumerable<string> zones, int defaultTtl)
    {
        var section = new JsonObject
        {
            [nameof(DnsSettings.Provider)] = string.IsNullOrWhiteSpace(provider) ? "infomaniak" : provider,
            [nameof(DnsSettings.ApiTokenRef)] = newRef ?? StoredRef(stored),
            [nameof(DnsSettings.AllowedZones)] = new JsonArray(zones.Select(z => (JsonNode?)z).ToArray()),
            [nameof(DnsSettings.DefaultTtl)] = Math.Clamp(defaultTtl, 60, 86400),
        };
        if (HidesEnvironmentToken(stored)) section[nameof(DnsSettings.ApiToken)] = "";
        else if (newRef is null && PlaintextToken(stored) is { } plaintext) section[nameof(DnsSettings.ApiToken)] = plaintext;
        return section;
    }

    private static string? Field(JsonObject? obj, string name)
        => obj?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
           && v.TryGetValue<string>(out var s) ? s : null;
}
