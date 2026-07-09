using System.Collections.Concurrent;
using Whiskers.Configuration;
using Whiskers.Models.Agent;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Services.Agent;

public sealed class AgentTranscript
{
    public List<AgentMessage> Messages { get; set; } = new();
}

/// <summary>Per-user persistence of the agent conversation (used both for display and as seed
/// history when starting a new session).</summary>
public interface IAgentTranscriptStore
{
    Task<List<AgentMessage>> LoadAsync(string userEmail);
    Task SaveAsync(string userEmail, IReadOnlyList<AgentMessage> messages);
    Task ClearAsync(string userEmail);
}

/// <summary>Persists the agent conversation per user (email hash) to disk so it survives reloads
/// and seeds a new session with context as history. Mirrors ChatHistoryStore.</summary>
public sealed class AgentTranscriptStore : IAgentTranscriptStore
{
    private const int KeepLast = 60;
    private readonly string _basePath;
    // Cache one JsonFileStore per path so its per-instance write-semaphore actually serializes concurrent
    // saves for the same user — a fresh store per call made that lock useless (lost update / IOException).
    private readonly ConcurrentDictionary<string, JsonFileStore<AgentTranscript>> _stores = new();

    public AgentTranscriptStore(string? basePath = null, DataPathOptions? dataPaths = null)
        => _basePath = basePath ?? (dataPaths ?? DataPathOptions.Default).AgentChatDir;

    private JsonFileStore<AgentTranscript> StoreFor(string userEmail)
        => _stores.GetOrAdd(GetPath(userEmail), p => new JsonFileStore<AgentTranscript>(p));

    public async Task<List<AgentMessage>> LoadAsync(string userEmail)
    {
        var store = StoreFor(userEmail);
        if (!store.Exists()) return new();
        var transcript = await store.LoadAsync();
        return transcript.Messages;
    }

    public async Task SaveAsync(string userEmail, IReadOnlyList<AgentMessage> messages)
    {
        var store = StoreFor(userEmail);
        await store.SaveAsync(new AgentTranscript { Messages = SanitizeForPersistence(messages) });
    }

    // Keep the last N messages, then make the window safe to persist AND re-seed: drop orphaned tool pairs
    // (a dangling tool_use/tool_result 400s the next provider request), redact tool outputs, and strip
    // base64 screenshots (prompt bloat + possible secrets).
    private static List<AgentMessage> SanitizeForPersistence(IReadOnlyList<AgentMessage> messages)
    {
        var trimmed = messages.TakeLast(KeepLast).ToList();

        // Drop leading Tool messages — after truncation the assistant tool_use they answer may be gone.
        var start = 0;
        while (start < trimmed.Count && trimmed[start].Role == AgentRole.Tool) start++;
        trimmed = trimmed.Skip(start).ToList();

        // Tool-call ids that still have a matching Tool result in the kept window.
        var answered = trimmed
            .Where(m => m.Role == AgentRole.Tool && m.ToolCallId != null)
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<AgentMessage>(trimmed.Count);
        foreach (var original in trimmed)
        {
            var m = original;
            if (m.Role == AgentRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                var kept = m.ToolCalls.Where(tc => answered.Contains(tc.Id)).ToList();
                m = m with { ToolCalls = kept.Count > 0 ? kept : null };
            }
            if (m.Role == AgentRole.Tool && m.Text != null)
                m = m with { Text = SecretRedactor.Redact(m.Text) };
            if (m.ImageBase64 != null)
                m = m with { ImageBase64 = null, ImageMediaType = null };
            result.Add(m);
        }
        return result;
    }

    public Task ClearAsync(string userEmail)
    {
        var path = GetPath(userEmail);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* egal */ }
        return Task.CompletedTask;
    }

    private string GetPath(string email)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..16];
        return Path.Combine(_basePath, $"{hash}.json");
    }
}
