using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services;
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
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor);
    event Action? Changed;
}

public sealed class AiTriggerStore : IAiTriggerStore, IInitializable
{
    private readonly JsonFileStore<AiTriggerData> _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AiTrigger> _triggers = new();

    public event Action? Changed;

    public AiTriggerStore(string? path = null, DataPathOptions? dataPaths = null)
        => _store = new JsonFileStore<AiTriggerData>(path ?? (dataPaths ?? DataPathOptions.Default).AiTriggersJson);

    public IReadOnlyList<AiTrigger> Triggers => _triggers;

    public int Order => 90;

    public async Task InitializeAsync(CancellationToken ct = default)
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
                $"AI triggers may only be changed by admins ({editor.DisplayName} is '{editor.PermissionLevel}').");

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
