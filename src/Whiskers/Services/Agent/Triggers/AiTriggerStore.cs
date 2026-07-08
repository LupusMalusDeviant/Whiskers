using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Agent.Triggers;

/// <summary>Serialized shape of ai-triggers.json.</summary>
public sealed class AiTriggerData
{
    public List<AiTrigger> Triggers { get; set; } = new();
}

/// <summary>Persists the AI triggers (ai-triggers.json). Writing is admin-only.</summary>
public interface IAiTriggerStore
{
    IReadOnlyList<AiTrigger> Triggers { get; }
    Task InitializeAsync();
    Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor);
    event Action? Changed;
}

public sealed class AiTriggerStore : IAiTriggerStore
{
    private readonly JsonFileStore<AiTriggerData> _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AiTrigger> _triggers = new();

    public event Action? Changed;

    public AiTriggerStore(string? path = null)
        => _store = new JsonFileStore<AiTriggerData>(path ?? "/app/data/ai-triggers.json");

    public IReadOnlyList<AiTrigger> Triggers => _triggers;

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_store.Exists())
                _triggers = (await _store.LoadAsync()).Triggers ?? new();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"AI-Trigger dürfen nur von Admins geändert werden ({editor.DisplayName} ist '{editor.PermissionLevel}').");

        await _lock.WaitAsync();
        try
        {
            _triggers = triggers;
            await _store.SaveAsync(new AiTriggerData { Triggers = triggers });
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }
}
