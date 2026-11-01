using System.Text.Json.Nodes;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent;

/// <summary>Writes the agent provider settings to /app/data/agent-settings.json in the form
/// { "Agent": { … } }. This file is wired in as a reloadOnChange config source (see Program.cs),
/// so IOptionsMonitor&lt;AgentSettings&gt; picks up changes without a restart. This makes the LLM
/// configurable via the UI — consistent with the app's other writable JSON stores (permissions, roles).
/// R1-30: the API key is kept in the vault; the file only names its vault key (<c>ApiKeyRef</c>), and the options read
/// it from the vault (<see cref="Whiskers.Services.Vault.VaultSecretsPostConfigure{T}"/>).</summary>
public interface IAgentSettingsStore
{
    /// <summary>Admin-only; runs through the Mutation Gateway as <c>config.agent</c> (R1-11). <paramref name="settings"/>
    /// never carries the API key (a plaintext key is refused); <paramref name="apiKey"/> says what happens to the stored
    /// one, decided under the store's lock (null: keep it). Returns the <c>Agent</c> section as it was before.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.agent")]
    Task<JsonObject?> SaveAsync(AgentSettings settings, AgentPrincipal editor, Whiskers.Services.Vault.SecretFieldChange? apiKey = null);

    /// <summary>The <c>Agent</c> section as stored in agent-settings.json, or null.</summary>
    Task<JsonObject?> ReadSectionAsync();

    /// <summary>R1-30 start-up migration: replaces the plaintext API key by its fixed migration key — only while the file
    /// still holds exactly <paramref name="plaintext"/> and names no vault key. Can only remove a secret from the file,
    /// so it is not a <c>config.agent</c>. Returns whether the file was rewritten.</summary>
    Task<bool> AdoptApiKeyRefAsync(string plaintext, string vaultRef);
}

public sealed class AgentSettingsStore : IAgentSettingsStore
{
    private readonly string _path;
    // One save at a time (R1-11): two interleaved saves must not move each other's document into place, so a save
    // that reports a failure never leaves its settings live.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentSettingsStore(string? path = null, DataPathOptions? dataPaths = null)
        => _path = path ?? (dataPaths ?? DataPathOptions.Default).AgentSettingsJson;

    public async Task<JsonObject?> SaveAsync(AgentSettings settings, AgentPrincipal editor, Whiskers.Services.Vault.SecretFieldChange? apiKey = null)
    {
        RequireAdmin(editor);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Agent:ApiKey is a credential kept in the vault; it is never written to agent-settings.json.");

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new JsonObject
        {
            ["Agent"] = new JsonObject
            {
                ["Enabled"] = settings.Enabled,
                ["Provider"] = settings.Provider,
                ["Model"] = settings.Model,
                ["Endpoint"] = settings.Endpoint ?? "",
                ["MaxToolIterations"] = settings.MaxToolIterations,
                ["MaxTokens"] = settings.MaxTokens,
                ["SystemPrompt"] = settings.SystemPrompt,
            }
        };

        await _lock.WaitAsync();
        try
        {
            var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                JsonNode? before = null;
                if (File.Exists(_path))
                    try { before = JsonNode.Parse(await File.ReadAllTextAsync(_path))?["Agent"]?.DeepClone(); }
                    catch (System.Text.Json.JsonException) { /* an unreadable file is no previous version */ }
                // The API key: decided against what is stored now (R1-30) — never a plaintext key from this save.
                var agent = (JsonObject)doc["Agent"]!;
                (apiKey ?? Whiskers.Services.Vault.SecretFieldChange.Keep).ApplyTo(agent, before as JsonObject, ApiKeyField);
                if (Whiskers.Services.Vault.VaultBackedSettings.Field(agent, ApiKeyField) is { } kept && !string.IsNullOrWhiteSpace(kept))
                    throw new InvalidOperationException(
                        "Agent:ApiKey is still stored in plaintext; it moves into the vault with a save once the vault is usable.");
                await File.WriteAllTextAsync(tmp, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
                // Written: the change as part of the gateway's event, the API key and prompt redacted (R1-22).
                Whiskers.Services.Mutations.ConfigChange.Record("agent",
                    before is null ? null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(before.ToJsonString()),
                    System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(doc["Agent"]!.ToJsonString()));
                return before as JsonObject;
            }
            finally
            {
                // Best effort: a leftover temp file must neither mask the write failure nor keep the lock held.
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private const string ApiKeyField = nameof(AgentSettings.ApiKey);

    /// <inheritdoc />
    public async Task<JsonObject?> ReadSectionAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return ReadRoot()?["Agent"]?.DeepClone() as JsonObject;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> AdoptApiKeyRefAsync(string plaintext, string vaultRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        if (vaultRef != Whiskers.Services.Vault.VaultBackedSettings.MigratedKey(Whiskers.Services.Vault.VaultBackedSettings.AgentSection, ApiKeyField))
            throw new ArgumentException("Only the agent API key's migration key is adopted.", nameof(vaultRef));
        await _lock.WaitAsync();
        try
        {
            var root = ReadRoot();
            if (root?["Agent"] is not JsonObject agent) return false;
            if (Whiskers.Services.Vault.VaultBackedSettings.PlaintextValue(agent, ApiKeyField) != plaintext) return false;
            Whiskers.Services.Vault.VaultBackedSettings.Remove(agent, ApiKeyField);
            Whiskers.Services.Vault.VaultBackedSettings.Remove(agent, Whiskers.Services.Vault.VaultBackedSettings.RefName(ApiKeyField));
            agent[Whiskers.Services.Vault.VaultBackedSettings.RefName(ApiKeyField)] = vaultRef;
            var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // An unreadable file reads as none: nothing to keep, nothing to migrate.
    private JsonObject? ReadRoot()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // The agent settings include the provider API key + system prompt (an injection vector) — persisting
    // them is admin-only, mirroring the guardrail store.
    private static void RequireAdmin(AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Agent settings may only be changed by admins ({editor.DisplayName} is '{editor.PermissionLevel}').");
    }
}
