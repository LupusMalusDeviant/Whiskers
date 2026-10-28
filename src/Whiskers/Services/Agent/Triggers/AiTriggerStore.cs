using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services;
using Whiskers.Services.Mutations;
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

    /// <summary>Replaces every trigger. The owner and the suspension are the store's (PC-D6, R1-13): whatever the
    /// caller sent for them is ignored. Assigning or changing a trigger's write grant makes
    /// <paramref name="editor"/> its owner, and so does switching a suspended trigger on again (a takeover).</summary>
    Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor);

    /// <summary>Switches trigger <paramref name="triggerId"/> off because its owner lost their authority — only while
    /// its owner is still <paramref name="expectedOwnerId"/> and it is on. <c>false</c> when an Admin took it over or
    /// switched it off meanwhile, or it is gone.</summary>
    Task<bool> SuspendAsync(string triggerId, string expectedOwnerId, string reason) => Task.FromResult(false);

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
            // A trigger saved before R1-13 has no write grant, so it is read-only now (PC-D7) — whatever MaxLevel it had.
            if (_store.Exists())
                _triggers = ((await _store.LoadAsync()).Triggers ?? new()).Where(t => t is not null).Select(Normalized).ToList();
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

        ArgumentNullException.ThrowIfNull(triggers);
        var editorId = PrincipalIds.Of(editor);
        await _lock.WaitAsync();
        try
        {
            var previous = _triggers.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var saved = new List<AiTrigger>(triggers.Count);
            foreach (var incoming in triggers)
            {
                var t = Normalized(incoming);
                previous.TryGetValue(t.Id, out var prev);
                t.OwnerId = prev?.OwnerId;
                t.SuspendedReason = prev?.SuspendedReason;

                var granted = t.WriteCapabilities.Count > 0 && t.WriteTargets.Count > 0;
                var grantChanged = prev is null
                                   || !prev.WriteCapabilities.ToHashSet(StringComparer.Ordinal).SetEquals(t.WriteCapabilities)
                                   || !prev.WriteTargets.ToHashSet(StringComparer.Ordinal).SetEquals(t.WriteTargets);
                // Switching a suspended trigger on again is a takeover: the Admin doing it owns it from now on.
                var takeover = t.SuspendedReason is not null && t.Enabled;
                if (takeover) t.SuspendedReason = null;
                if (!granted) t.OwnerId = null;                       // read-only: nothing runs on anyone's authority
                else if (grantChanged || takeover || string.IsNullOrWhiteSpace(t.OwnerId))
                {
                    if (editorId == PrincipalIds.Anonymous)
                        throw new UnauthorizedAccessException("A write grant needs an editor with an identity to own it.");
                    t.OwnerId = editorId;
                }
                saved.Add(t);
            }
            await _store.SaveAsync(new AiTriggerData { Triggers = saved });
            _triggers = saved;
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task<bool> SuspendAsync(string triggerId, string expectedOwnerId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerId);
        await _lock.WaitAsync();
        try
        {
            var current = _triggers.FirstOrDefault(t => t.Id == triggerId);
            if (current is null || !current.Enabled || current.OwnerId != expectedOwnerId) return false;
            var suspended = Normalized(current);
            suspended.Enabled = false;
            suspended.SuspendedReason = string.IsNullOrWhiteSpace(reason) ? "its owner lost their authority" : reason;
            var saved = _triggers.Select(t => t.Id == triggerId ? suspended : t).ToList();
            await _store.SaveAsync(new AiTriggerData { Triggers = saved });
            _triggers = saved;
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>A copy the store owns: the caller's list stays the caller's, and the grant lists are trimmed and
    /// free of duplicates.</summary>
    internal static AiTrigger Normalized(AiTrigger s) => new()
    {
        Id = string.IsNullOrWhiteSpace(s.Id) ? Guid.NewGuid().ToString("N") : s.Id,
        Name = s.Name ?? "",
        Enabled = s.Enabled,
        EventTypes = (s.EventTypes ?? new()).ToList(),
        NameFilter = s.NameFilter ?? "",
        Prompt = s.Prompt ?? "",
        GuardrailPreset = s.GuardrailPreset ?? "",
        WriteCapabilities = Clean(s.WriteCapabilities),
        WriteTargets = Clean(s.WriteTargets),
        OwnerId = s.OwnerId,
        SuspendedReason = s.SuspendedReason,
        CooldownSeconds = s.CooldownSeconds,
    };

    private static List<string> Clean(List<string>? values) =>
        (values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
}
