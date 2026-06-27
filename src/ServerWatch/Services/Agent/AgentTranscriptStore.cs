using ServerWatch.Models.Agent;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Agent;

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

    public AgentTranscriptStore(string? basePath = null)
        => _basePath = basePath ?? "/app/data/agent-chat";

    public async Task<List<AgentMessage>> LoadAsync(string userEmail)
    {
        var store = new JsonFileStore<AgentTranscript>(GetPath(userEmail));
        if (!store.Exists()) return new();
        var transcript = await store.LoadAsync();
        return transcript.Messages;
    }

    public async Task SaveAsync(string userEmail, IReadOnlyList<AgentMessage> messages)
    {
        var store = new JsonFileStore<AgentTranscript>(GetPath(userEmail));
        await store.SaveAsync(new AgentTranscript { Messages = messages.TakeLast(KeepLast).ToList() });
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
