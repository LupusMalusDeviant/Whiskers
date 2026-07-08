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
    Task SaveAsync(AgentSettings settings, AgentPrincipal editor);
}

public sealed class AgentSettingsStore : IAgentSettingsStore
{
    private readonly string _path;

    public AgentSettingsStore(string? path = null)
        => _path = path ?? "/app/data/agent-settings.json";

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

        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    // The agent settings include the provider API key + system prompt (an injection vector) — persisting
    // them is admin-only, mirroring the guardrail store.
    private static void RequireAdmin(AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Agent-Einstellungen dürfen nur von Admins geändert werden ({editor.DisplayName} ist '{editor.PermissionLevel}').");
    }
}
