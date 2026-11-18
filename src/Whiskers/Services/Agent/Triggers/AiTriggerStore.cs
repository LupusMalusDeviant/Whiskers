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

    /// <summary>Changes with every write (a save, a suspension, a reload). A save names the revision its list was
    /// read at, so an Admin's stale copy never undoes a suspension or a narrowed grant made meanwhile.</summary>
    long Revision { get; }

    /// <summary>Replaces every trigger. The owner and the suspension are the store's (PC-D6, R1-13): whatever the
    /// caller sent for them is ignored. <paramref name="editor"/> becomes the owner of a trigger it creates, of one
    /// whose write grant it changes, and of one it switches on again after a suspension or without an owner (a
    /// takeover). Every other trigger keeps its owner. Throws <see cref="AiTriggersChangedException"/> when the
    /// triggers changed since <paramref name="readAtRevision"/>.</summary>
    [MutatingOperation("aitrigger.save")]
    Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision);

    /// <summary>Switches trigger <paramref name="triggerId"/> off because its owner lost their authority (or it has
    /// none: <paramref name="expectedOwnerId"/> <c>null</c>) — only while its owner is still that one and it is on.
    /// <c>false</c> when an Admin took it over or switched it off meanwhile, or it is gone.</summary>
    Task<bool> SuspendAsync(string triggerId, string? expectedOwnerId, string reason) => Task.FromResult(false);

    /// <summary>Contract 0020: <c>OwnerId := to</c> only while it is <paramref name="fromOwnerId"/>; <c>Enabled := false</c>;
    /// <c>SuspendedReason := null</c> (D7: switching it on later is no takeover). Called only from
    /// <c>AutomationHandover</c>.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverWrite> HandOverAsync(string triggerId, string fromOwnerId, string toOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hand a trigger over.");

    /// <summary>Contract 0020 (D11 c): <c>Enabled := false</c>, <c>SuspendedReason := null</c>, only while the owner is
    /// <paramref name="ownerId"/>; the owner stays. Inside <c>account.create</c> only.</summary>
    [MutatingOperation("automation.hold")]
    Task<HandoverWrite> HoldAsync(string triggerId, string ownerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hold a trigger.");

    event Action? Changed;
}

/// <summary>The AI triggers changed since the list being saved was read: nothing was saved.</summary>
public sealed class AiTriggersChangedException()
    : InvalidOperationException("The AI triggers were changed meanwhile; nothing was saved. Reload and apply the change again.");

public sealed class AiTriggerStore : IAiTriggerStore, IInitializable
{
    private readonly JsonFileStore<AiTriggerData> _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AiTrigger> _triggers = new();

    public event Action? Changed;

    // Contract 0020: the first-run ledger of handed-over triggers.
    private readonly IHandoverLedger? _handovers;

    public AiTriggerStore(string? path = null, DataPathOptions? dataPaths = null, IHandoverLedger? handovers = null)
    {
        _store = new JsonFileStore<AiTriggerData>(path ?? (dataPaths ?? DataPathOptions.Default).AiTriggersJson);
        _handovers = handovers;
    }

    private static AutomationItemRef ItemOf(string triggerId) => new(AutomationKind.AiTrigger, triggerId);

    public IReadOnlyList<AiTrigger> Triggers => _triggers;

    public long Revision => Interlocked.Read(ref _revision);
    private long _revision;

    public int Order => 90;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync();
        try
        {
            // A trigger saved before R1-13 has no write grant, so it is read-only now (PC-D7) — whatever MaxLevel it had.
            if (_store.Exists())
            {
                _triggers = ((await _store.LoadAsync()).Triggers ?? new()).Where(t => t is not null).Select(Normalized).ToList();
                Interlocked.Increment(ref _revision);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"AI triggers may only be changed by admins ({editor.DisplayName} is '{editor.PermissionLevel}').");

        ArgumentNullException.ThrowIfNull(triggers);
        var editorId = PrincipalIds.Of(editor);
        await _lock.WaitAsync();
        try
        {
            if (readAtRevision != Revision) throw new AiTriggersChangedException();
            var previous = _triggers.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var saved = new List<AiTrigger>(triggers.Count);
            var forget = new List<string>();
            var resumed = new List<string>();
            foreach (var incoming in triggers)
            {
                var t = Normalized(incoming);
                t.WriteCapabilities.RemoveAll(AiTriggerAuthority.NotGrantable.Contains);
                previous.TryGetValue(t.Id, out var prev);
                t.OwnerId = prev?.OwnerId;
                t.SuspendedReason = prev?.SuspendedReason;

                var grantChanged = prev is null
                                   || !prev.WriteCapabilities.ToHashSet(StringComparer.Ordinal).SetEquals(t.WriteCapabilities)
                                   || !prev.WriteTargets.ToHashSet(StringComparer.Ordinal).SetEquals(t.WriteTargets);
                // Switching a suspended or ownerless trigger on is a takeover: the Admin doing it owns it from now on.
                // A trigger saved before R1-13 has no owner; its first run suspends it until an Admin does that.
                var takeover = t.Enabled && prev is not null
                               && (t.SuspendedReason is not null || (string.IsNullOrWhiteSpace(t.OwnerId) && !prev.Enabled));
                if (takeover) t.SuspendedReason = null;
                if (prev is null || grantChanged || takeover)
                {
                    if (editorId == PrincipalIds.Anonymous)
                        throw new UnauthorizedAccessException("An AI trigger needs an editor with an identity to own it.");
                    t.OwnerId = editorId;
                }
                // Contract 0020: a new owner ends a handover's note (a takeover as today); switching a trigger on with
                // its owner kept is its resume (D7).
                if (prev is not null && !string.Equals(prev.OwnerId, t.OwnerId, StringComparison.Ordinal)) forget.Add(t.Id);
                else if (prev is { Enabled: false } && t.Enabled) resumed.Add(t.Id);
                saved.Add(t);
            }
            forget.AddRange(previous.Keys.Where(id => saved.All(s => s.Id != id)));   // deleted
            var before = new AiTriggerData { Triggers = _triggers };
            await _store.SaveAsync(new AiTriggerData { Triggers = saved });
            _triggers = saved;
            Interlocked.Increment(ref _revision);
            Whiskers.Services.Mutations.ConfigChange.Record("ai-triggers", before, new AiTriggerData { Triggers = saved });   // R1-22
            foreach (var id in forget) HandoverNotes.ForgetQuietly(_handovers, ItemOf(id));
            foreach (var id in resumed) HandoverNotes.RecordResumeInScope(_handovers, ItemOf(id));
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task<bool> SuspendAsync(string triggerId, string? expectedOwnerId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        await _lock.WaitAsync();
        try
        {
            var current = _triggers.FirstOrDefault(t => t.Id == triggerId);
            if (current is null || !current.Enabled
                || !string.Equals(NullIfEmpty(current.OwnerId), NullIfEmpty(expectedOwnerId), StringComparison.Ordinal))
                return false;
            var suspended = Normalized(current);
            suspended.Enabled = false;
            suspended.SuspendedReason = string.IsNullOrWhiteSpace(reason) ? "its owner lost their authority" : reason;
            var saved = _triggers.Select(t => t.Id == triggerId ? suspended : t).ToList();
            var before = new AiTriggerData { Triggers = _triggers };
            await _store.SaveAsync(new AiTriggerData { Triggers = saved });
            _triggers = saved;
            Interlocked.Increment(ref _revision);
            Whiskers.Services.Mutations.ConfigChange.Record("ai-triggers", before, new AiTriggerData { Triggers = saved });   // R1-22
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
        return true;
    }

    public Task<HandoverWrite> HandOverAsync(string triggerId, string fromOwnerId, string toOwnerId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        return SwitchOffAsync(triggerId, fromOwnerId, toOwnerId, ct);
    }

    public Task<HandoverWrite> HoldAsync(string triggerId, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return SwitchOffAsync(triggerId, ownerId, ownerId, ct);
    }

    /// <summary>Contract 0020: owner <paramref name="from"/> → <paramref name="to"/> (the same for a hold), off, and no
    /// suspension reason — so switching it on later keeps the owner (D7).</summary>
    private async Task<HandoverWrite> SwitchOffAsync(string triggerId, string from, string to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        HandoverWrite result;
        await _lock.WaitAsync(ct);
        try
        {
            var current = _triggers.FirstOrDefault(t => t.Id == triggerId);
            if (current is null) return new(HandoverItemStatus.SkippedGone, triggerId, false);
            if (!string.Equals(current.OwnerId, from, StringComparison.Ordinal))
                return new(HandoverItemStatus.SkippedOwnerChanged, current.Name, current.Enabled);
            var changed = Normalized(current);
            changed.OwnerId = to;
            changed.Enabled = false;
            changed.SuspendedReason = null;
            var saved = _triggers.Select(t => t.Id == triggerId ? changed : t).ToList();
            var before = new AiTriggerData { Triggers = _triggers };
            await _store.SaveAsync(new AiTriggerData { Triggers = saved });
            _triggers = saved;
            Interlocked.Increment(ref _revision);
            Whiskers.Services.Mutations.ConfigChange.Record("ai-triggers", before, new AiTriggerData { Triggers = saved });   // R1-22
            result = new(HandoverItemStatus.HandedOver, current.Name, current.Enabled);
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
        return result;
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> Clean(List<string>? values) =>
        (values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
}
