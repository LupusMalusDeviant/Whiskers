using System.Text.Json;
using System.Text.Json.Nodes;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>Writes UI-edited settings into /app/data/app-settings.json, the last configuration
/// layer (overrides env, reloadOnChange) — so changes apply live via IOptionsMonitor without a
/// restart. One section per settings class (e.g. "Mattermost", "MetricAlert").</summary>
public interface IAppSettingsStore
{
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveSectionAsync<T>(string section, T value);

    /// <summary>Writes several sections in one file replacement (R1-11): either all of them take effect or, when
    /// the write fails, none.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections);

    /// <summary>The section as stored in app-settings.json (not merged with other configuration layers), or null.</summary>
    Task<JsonObject?> ReadSectionAsync(string section);

    /// <summary>R1-29 start-up migration: replaces the plaintext DNS token of app-settings.json by the vault key
    /// <paramref name="vaultRef"/> — only while the file still holds exactly <paramref name="plaintext"/> and names no
    /// vault key. It changes nothing else and can only remove a secret from the file, never write one, so it is not a
    /// <c>config.save</c>. Returns whether the file was rewritten.</summary>
    Task<bool> AdoptDnsTokenRefAsync(string plaintext, string vaultRef);
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettingsStore(string? path = null, DataPathOptions? dataPaths = null)
        => _path = path ?? (dataPaths ?? DataPathOptions.Default).AppSettingsJson;

    /// <summary>Credentials that live in the vault (R1-29, INV-52): a section carrying one of these fields with a value is
    /// refused, so no save can put the secret back into the file.</summary>
    private static readonly (string Section, string Field)[] VaultOnlyFields =
    [
        (DnsSettings.SectionName, nameof(DnsSettings.ApiToken)),
    ];

    public Task SaveSectionAsync<T>(string section, T value) =>
        SaveSectionsAsync(new Dictionary<string, object> { [section] = JsonSerializer.SerializeToNode(value)! });

    /// <inheritdoc />
    public async Task<JsonObject?> ReadSectionAsync(string section)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadRootAsync())[section]?.DeepClone() as JsonObject;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> AdoptDnsTokenRefAsync(string plaintext, string vaultRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        if (vaultRef != Whiskers.Services.Vault.CredentialRefs.MigratedDnsToken)
            throw new ArgumentException("Only the DNS token's migration key is adopted.", nameof(vaultRef));
        await _lock.WaitAsync();
        try
        {
            var root = await ReadRootAsync();
            if (root[DnsSettings.SectionName] is not JsonObject dns) return false;
            if (!string.IsNullOrWhiteSpace(Field(dns, nameof(DnsSettings.ApiTokenRef)))) return false;
            var tokenName = dns.FirstOrDefault(p => string.Equals(p.Key, nameof(DnsSettings.ApiToken), StringComparison.OrdinalIgnoreCase)).Key;
            if (tokenName is null || Field(dns, tokenName) != plaintext) return false;
            dns.Remove(tokenName);
            dns[nameof(DnsSettings.ApiTokenRef)] = vaultRef;
            await WriteRootAsync(root);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string? Field(JsonObject obj, string name)
        => obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
           && v.TryGetValue<string>(out var s) ? s : null;

    private async Task<JsonObject> ReadRootAsync()
        => File.Exists(_path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(_path)) as JsonObject ?? new JsonObject()
            : new JsonObject();

    private async Task WriteRootAsync(JsonObject root)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private static void EnsureNoVaultOnlyValue(string section, JsonNode? value)
    {
        // A flattened key ("Dns:ApiToken") is read by the configuration as the nested path: never write one.
        if (section.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException($"Section '{section}' is not a section name.");
        foreach (var (s, field) in VaultOnlyFields)
        {
            if (!string.Equals(s, section, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is null) continue;
            if (value is not JsonObject obj)
                throw new InvalidOperationException($"Section '{section}' must be an object.");
            var hit = obj.FirstOrDefault(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)).Value;
            if (hit is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"{section}:{field} is a credential kept in the vault; it is never written to app-settings.json.");
        }
    }

    public async Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        // Refused before anything is read or written: a credential that belongs in the vault never reaches the file.
        var nodes = sections.ToDictionary(s => s.Key, s => s.Value as JsonNode ?? JsonSerializer.SerializeToNode(s.Value, s.Value.GetType()));
        foreach (var (section, node) in nodes) EnsureNoVaultOnlyValue(section, node);

        await _lock.WaitAsync();
        try
        {
            var root = await ReadRootAsync();

            var changes = new List<(string Section, JsonNode? Before, JsonNode? After)>();
            foreach (var (section, after) in nodes)
            {
                changes.Add((section, root[section]?.DeepClone(), after?.DeepClone()));
                root[section] = after;
            }

            await WriteRootAsync(root);

            // Written: what changed goes into the gateway's event, versioned and redacted (R1-22).
            foreach (var (section, before, after) in changes)
                Whiskers.Services.Mutations.ConfigChange.Record(section, ToElement(before), ToElement(after));
        }
        finally
        {
            _lock.Release();
        }
    }

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
}
