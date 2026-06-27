using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Agent.Guardrails;

/// <summary>Persists the GuardrailPolicy in its OWN file (guardrails.json), separate from the
/// agent settings path. Writing is admin-only — a compromised settings editor cannot
/// relax the guardrails. Engine/callers read exclusively from Current.</summary>
public sealed class GuardrailStore : IGuardrailStore
{
    private readonly JsonFileStore<GuardrailPolicy> _store;
    private readonly ILogger<GuardrailStore>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GuardrailPolicy _current = GuardrailPolicy.SafeDefault();

    public event Action? Changed;

    public GuardrailStore(ILogger<GuardrailStore>? logger = null, string? filePath = null)
    {
        _logger = logger;
        _store = new JsonFileStore<GuardrailPolicy>(filePath ?? "/app/data/guardrails.json");
    }

    public GuardrailPolicy Current => _current;

    /// <summary>Loads the policy from disk; creates the SafeDefault on first run.</summary>
    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_store.Exists())
            {
                _current = GuardrailPolicy.SafeDefault();
                await _store.SaveAsync(_current);
                _logger?.LogInformation("Guardrails: SafeDefault angelegt.");
            }
            else
            {
                _current = await _store.LoadAsync();
                _logger?.LogInformation("Guardrails geladen (MaxAutonomousLevel={Level}, ReadOnly={RO}).",
                    _current.MaxAutonomousLevel, _current.ReadOnlyMode);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Guardrails dürfen nur von Admins geändert werden ({editor.DisplayName} ist '{editor.PermissionLevel}').");

        await _lock.WaitAsync(ct);
        try
        {
            await _store.SaveAsync(policy);
            _current = policy;
            _logger?.LogWarning("Guardrails geändert von {Editor}.", editor.DisplayName);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }
}
