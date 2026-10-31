using System.Text.Json.Nodes;

namespace Whiskers.Services.Vault;

/// <summary>
/// The credentials of the notification channels and the AI clients that R1-30 moved out of the settings files (INV-52).
/// Each named property of a section is kept in the vault: the file only names its vault key in a sibling property
/// <c>&lt;Property&gt;Ref</c>, and the options read the value from the vault when they are bound
/// (<see cref="VaultSecretsPostConfigure{T}"/>). A save writes a new value under a NEW key that the section then names,
/// so a failed save leaves the previous credential in use; the start-up migration uses one fixed key per credential.
/// </summary>
public static class VaultBackedSettings
{
    /// <summary>The agent's section: it lives in agent-settings.json, all others in app-settings.json.</summary>
    public const string AgentSection = "Agent";

    /// <summary>Section → the properties kept in the vault.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Mattermost"] = ["WebhookUrl"],
        ["Matrix"] = ["AccessToken"],
        ["Telegram"] = ["BotToken"],
        ["Ntfy"] = ["Topic", "Token"],
        ["Discord"] = ["WebhookUrl"],
        ["Slack"] = ["WebhookUrl"],
        ["Email"] = ["Password"],
        ["WebhookNotification"] = ["Url", "Secret"],
        ["AiChat"] = ["ApiKey"],
        [AgentSection] = ["ApiKey"],
    };

    /// <summary>The sections of app-settings.json (every one but the agent's).</summary>
    public static IEnumerable<string> AppSettingsSections => Fields.Keys.Where(s => !IsAgent(s));

    public static bool IsAgent(string section) => string.Equals(section, AgentSection, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="property"/> of <paramref name="section"/> is kept in the vault.</summary>
    public static bool IsVaultBacked(string section, string property)
        => Fields.TryGetValue(section, out var props) && props.Contains(property, StringComparer.OrdinalIgnoreCase);

    /// <summary>The property that names the vault key of <paramref name="property"/>.</summary>
    public static string RefName(string property) => property + "Ref";

    private static string Prefix(string section, string property)
        => $"settings:{section.ToLowerInvariant()}:{property.ToLowerInvariant()}:";

    /// <summary>A new vault key for <paramref name="property"/> of <paramref name="section"/>.</summary>
    public static string NewKey(string section, string property) => Prefix(section, property) + Guid.NewGuid().ToString("N");

    /// <summary>The vault key the migration writes a legacy plaintext value to.</summary>
    public static string MigratedKey(string section, string property) => Prefix(section, property) + "migrated";

    /// <summary>Whether <paramref name="key"/> is a fixed migration key of a vault-backed setting.</summary>
    public static bool IsMigrationKey(string key)
        => Fields.Any(f => f.Value.Any(p => key == MigratedKey(f.Key, p)));

    /// <summary>The value of <paramref name="name"/> in <paramref name="section"/>, matched case-insensitively.</summary>
    public static string? Field(JsonObject? section, string name)
        => section?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
           && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The vault key a stored section names for <paramref name="property"/>, or null.</summary>
    public static string? StoredRef(JsonObject? section, string property)
        => Field(section, RefName(property)) is { Length: > 0 } r && !string.IsNullOrWhiteSpace(r) ? r : null;

    /// <summary>A plaintext value still stored for <paramref name="property"/> (legacy, before R1-30), when the section
    /// names no vault key for it.</summary>
    public static string? PlaintextValue(JsonObject? section, string property)
        => StoredRef(section, property) is null && Field(section, property) is { } v && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Removes <paramref name="name"/> from <paramref name="section"/> under every spelling.</summary>
    public static void Remove(JsonObject section, string name)
    {
        foreach (var key in section.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
            section.Remove(key);
    }
}

/// <summary>
/// How a save changes one vault-backed setting. The store decides it under its write lock against the section as stored
/// then: <see cref="Keep"/> keeps the stored reference (and an empty value that hides one set by the environment), so a
/// stale page can neither point back to an old key nor write plaintext.
/// </summary>
public sealed record SecretFieldChange
{
    private SecretFieldChange(string? newRef, bool clear, string? movedPlaintext)
    {
        NewRef = newRef;
        Clears = clear;
        MovedPlaintext = movedPlaintext;
    }

    /// <summary>The vault key that holds the new value, or null.</summary>
    public string? NewRef { get; }

    /// <summary>Whether the value is removed.</summary>
    public bool Clears { get; }

    /// <summary>For a moved legacy value: the plaintext the save read and stored in the vault.</summary>
    public string? MovedPlaintext { get; }

    public static SecretFieldChange Keep { get; } = new(null, false, null);

    /// <summary>Remove the value; an empty value is written, so one set by the environment stays hidden as before.</summary>
    public static SecretFieldChange Clear { get; } = new(null, true, null);

    /// <summary>Use the value stored in the vault under <paramref name="vaultRef"/>.</summary>
    public static SecretFieldChange Set(string vaultRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRef);
        return new(vaultRef, false, null);
    }

    /// <summary>Use <paramref name="vaultRef"/>, which holds the legacy plaintext <paramref name="movedPlaintext"/> the save
    /// read — only while the stored section still holds exactly it.</summary>
    public static SecretFieldChange Move(string vaultRef, string movedPlaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(movedPlaintext);
        return new(vaultRef, false, movedPlaintext);
    }

    /// <summary>Writes the setting into <paramref name="next"/> (a section without it), against <paramref name="stored"/>.
    /// Never a plaintext value: Keep copies only what is stored, and a stored legacy plaintext is copied as it is, so the
    /// store's guard refuses the write instead of the value silently leaving the file without being in the vault.</summary>
    public void ApplyTo(JsonObject next, JsonObject? stored, string property)
    {
        ArgumentNullException.ThrowIfNull(next);
        var refName = VaultBackedSettings.RefName(property);
        VaultBackedSettings.Remove(next, property);
        VaultBackedSettings.Remove(next, refName);

        if (MovedPlaintext is not null && VaultBackedSettings.PlaintextValue(stored, property) != MovedPlaintext)
            throw new SecretChangedException();
        if (NewRef is not null)
        {
            next[refName] = NewRef;
            return;
        }
        if (Clears)
        {
            next[property] = "";
            return;
        }
        if (VaultBackedSettings.StoredRef(stored, property) is { } storedRef) next[refName] = storedRef;
        else if (VaultBackedSettings.Field(stored, property) is { } value) next[property] = value;
    }

    /// <summary>A moved legacy value no longer matches the stored section: another save changed it meanwhile.</summary>
    public sealed class SecretChangedException()
        : InvalidOperationException("A credential was changed meanwhile; reload and save again.");
}
