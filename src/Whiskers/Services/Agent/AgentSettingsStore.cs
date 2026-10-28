using System.Text.Json.Nodes;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent;

/// <summary>Writes the agent provider settings to /app/data/agent-settings.json in the form
/// { "Agent": { … } }. This file is wired in as a reloadOnChange config source (see Program.cs),
/// so IOptionsMonitor&lt;AgentSettings&gt; picks up changes without a restart. This makes the LLM
/// configurable via the UI — consistent with the app's other writable JSON stores (permissions, roles).
/// Note: ApiKey is stored in plaintext under /app/data (same choice as servers.json); the editor is admin-only.</summary>
public interface IAgentSettingsStore
{
    /// <summary>Admin-only; runs through the Mutation Gateway as <c>config.agent</c> (R1-11).</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.agent")]
    Task SaveAsync(AgentSettings settings, AgentPrincipal editor);
}

public sealed class AgentSettingsStore : IAgentSettingsStore
{
    private readonly string _path;
    // One save at a time (R1-11): two interleaved saves must not move each other's document into place, so a save
    // that reports a failure never leaves its settings live.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentSettingsStore(string? path = null, DataPathOptions? dataPaths = null)
        => _path = path ?? (dataPaths ?? DataPathOptions.Default).AgentSettingsJson;

    public async Task SaveAsync(AgentSettings settings, AgentPrincipal editor)
    {
        RequireAdmin(editor);

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
                ["ApiKey"] = settings.ApiKey,
                ["MaxToolIterations"] = settings.MaxToolIterations,
                ["MaxTokens"] = settings.MaxTokens,
                ["SystemPrompt"] = settings.SystemPrompt,
            }
        };

        await _lock.WaitAsync();
        var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            _lock.Release();
        }
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
