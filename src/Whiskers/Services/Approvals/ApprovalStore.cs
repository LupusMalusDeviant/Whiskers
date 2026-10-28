using System.Collections.Concurrent;
using Whiskers.Models;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Approvals;

/// <summary>Central registry of Human-in-the-Loop approvals. A <c>Confirm</c> raises an approval here; the
/// "Approvals" page and the notification bell read pending ones and call <see cref="ResolveAsync"/>. Every approval
/// is persisted (R1-14) before anything acts on it — raised, decided, used — so decisions and single use survive a
/// restart, and the history is never truncated. An interactive approval also carries a resolver that reaches the
/// live agent session waiting on it; a restart cancels such an approval, since the session does not survive it.</summary>
public interface IApprovalStore
{
    /// <summary>Registers and persists a pending approval. <paramref name="resolver"/> is invoked with the human's
    /// decision (true = approve) exactly once when the approval is resolved. Throws when it cannot be persisted:
    /// an approval nobody could find again is never raised.</summary>
    Task<Approval> CreateAsync(ApprovalRequest request, Func<bool, Task> resolver, CancellationToken ct = default);

    /// <summary>Pending approvals (newest first), with overdue ones swept to Expired first.</summary>
    IReadOnlyList<Approval> GetPending();

    /// <summary>Open approvals and the ones decided recently in this process, newest first. The complete history is
    /// <see cref="GetHistoryAsync"/>.</summary>
    IReadOnlyList<Approval> GetAll();

    /// <summary>The complete persisted history, newest first.</summary>
    Task<IReadOnlyList<Approval>> GetHistoryAsync(int skip, int take, CancellationToken ct = default);

    Approval? Get(string approvalId);

    /// <summary>The approval raised for a call with this <see cref="Approval.Binding"/> that can still be decided or
    /// used (pending and not expired, or approved and not used yet), newest first; <c>null</c> when there is none.</summary>
    Approval? FindOpen(string binding);

    /// <summary>Approve/reject a pending approval. Invokes its resolver. Returns false if not pending.
    /// When <paramref name="resolverLevel"/> is supplied (read/write/admin), the resolver must hold at
    /// least the approval's own level — a lower-privileged user cannot approve a higher-privileged action.
    /// Pass null only for system-internal resolves (expiry/cancel).</summary>
    Task<bool> ResolveAsync(string approvalId, bool approved, string? resolvedBy, string? resolverLevel = null);

    /// <summary>Uses an approved approval once, for operation <paramref name="operationId"/>. <c>false</c> when it is
    /// not approved, was used already (by anyone, before or after a restart) or its grant lapsed at
    /// <paramref name="now"/>.</summary>
    Task<bool> TryConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default);

    /// <summary>Cancels any pending approvals for a session that ended/aborted (resolver gets false).</summary>
    Task CancelForSessionAsync(string sessionId);

    /// <summary>Loads the open approvals after a restart (the database must be migrated). An interactive pending
    /// approval is cancelled: its session is gone.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Raised whenever the pending set changes (create/resolve/expire) — drives bell + page.</summary>
    event Action? Changed;
}

public sealed class ApprovalStore : IApprovalStore
{
    /// <summary>How long an approval stays usable after it was approved.</summary>
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);

    // Decided approvals kept in memory for the page; the database keeps all of them.
    private const int MaxRetainedDecided = 200;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly IApprovalPersistence _persistence;
    private readonly ILogger<ApprovalStore>? _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    public ApprovalStore(ILogger<ApprovalStore>? logger = null, TimeSpan? approvalTtl = null,
        IApprovalPersistence? persistence = null, TimeProvider? time = null)
    {
        _logger = logger;
        _ttl = approvalTtl ?? TimeSpan.FromMinutes(15);
        _persistence = persistence ?? new InMemoryApprovalPersistence();
        _time = time ?? TimeProvider.System;
    }

    public event Action? Changed;

    private sealed class Entry
    {
        public Entry(Approval approval, Func<bool, Task> resolver) { Approval = approval; Resolver = resolver; }
        public Approval Approval { get; }
        public Func<bool, Task> Resolver { get; }
        // 0 = not yet resolved; flipped to 1 via Interlocked so the resolver runs exactly once.
        public int Resolved;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Approval> CreateAsync(ApprovalRequest request, Func<bool, Task> resolver, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolver);
        var now = Now;
        var approval = new Approval
        {
            SessionId = request.SessionId,
            ToolCallId = request.ToolCallId,
            Actor = request.Actor,
            ActorType = request.ActorType,
            ToolName = request.ToolName,
            Level = request.Level,
            ParamsJson = request.ParamsJson,
            Reason = request.Reason,
            Diff = request.Diff,
            CreatedAt = now,
            ExpiresAt = now.Add(_ttl),
            CorrelationId = request.CorrelationId,
            RiskLevel = request.RiskLevel,
            TargetServer = request.TargetServer,
            TargetWorkload = request.TargetWorkload,
            GuardrailPreset = request.GuardrailPreset,
            GuardrailRule = request.GuardrailRule,
            ArgumentsHash = request.ArgumentsHash,
            DecisionId = request.DecisionId,
            Binding = request.Binding,
            Interactive = request.Interactive,
        };
        // Persisted first: an approval that is not on record is never shown, decided or used.
        await _persistence.InsertAsync(approval, ct);
        _entries[approval.Id] = new Entry(approval, resolver);
        TrimDecided();
        Changed?.Invoke();
        return approval;
    }

    public IReadOnlyList<Approval> GetPending()
    {
        SweepExpired();
        return _entries.Values
            .Select(e => e.Approval)
            .Where(a => a.Status == ApprovalStatus.Pending)
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public IReadOnlyList<Approval> GetAll()
    {
        SweepExpired();
        return _entries.Values
            .Select(e => e.Approval)
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public Task<IReadOnlyList<Approval>> GetHistoryAsync(int skip, int take, CancellationToken ct = default) =>
        _persistence.LoadHistoryAsync(skip, take, ct);

    public Approval? Get(string approvalId) =>
        _entries.TryGetValue(approvalId, out var e) ? e.Approval : null;

    public Approval? FindOpen(string binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        SweepExpired();
        var now = Now;
        return _entries.Values
            .Select(e => e.Approval)
            .Where(a => a.Binding == binding
                        && (a.Status == ApprovalStatus.Pending
                            || (a.Status == ApprovalStatus.Approved && a.ConsumedAt is null && a.GrantExpiresAt > now)))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();
    }

    public async Task<bool> ResolveAsync(string approvalId, bool approved, string? resolvedBy, string? resolverLevel = null)
    {
        if (!_entries.TryGetValue(approvalId, out var entry) || entry.Approval.Status != ApprovalStatus.Pending)
            return false;

        // Approving is itself a privileged action: a resolver may only approve an action whose level it
        // could perform. Rejecting is always allowed (it only makes things safer).
        if (approved && resolverLevel != null
            && !McpPermissionLevels.HasAccess(resolverLevel, entry.Approval.Level))
        {
            _logger?.LogWarning("Approval {Id} ({Tool}, level={Level}) approval denied: resolver level {ResolverLevel} is insufficient",
                approvalId, entry.Approval.ToolName, entry.Approval.Level, resolverLevel);
            return false;
        }

        // Exactly-once: only the thread that flips Resolved 0→1 performs the transition + resolver.
        if (Interlocked.CompareExchange(ref entry.Resolved, 1, 0) != 0)
            return false;

        var status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        if (!await PersistAsync(entry, status, resolvedBy, mustStore: true))
        {
            // Not on record (or decided elsewhere first), not decided here: the approval stays pending in this store
            // and nothing waiting on it is released.
            Interlocked.Exchange(ref entry.Resolved, 0);
            return false;
        }
        await SafeResolve(entry, approved);
        Changed?.Invoke();
        return true;
    }

    public async Task<bool> TryConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!_entries.TryGetValue(approvalId, out var entry)) return false;
        var approval = entry.Approval;
        if (approval.Status != ApprovalStatus.Approved || approval.ConsumedAt is not null
            || approval.GrantExpiresAt is not { } until || until <= now)
            return false;
        // The database decides who wins: one conditional update, whichever process or consumer asks.
        if (!await _persistence.ConsumeAsync(approvalId, operationId, now, ct)) return false;
        approval.ConsumedAt = now;
        approval.ConsumedBy = operationId;
        Changed?.Invoke();
        return true;
    }

    public async Task CancelForSessionAsync(string sessionId)
    {
        var affected = _entries.Values
            .Where(e => e.Approval.SessionId == sessionId && e.Approval.Status == ApprovalStatus.Pending)
            .ToList();
        if (affected.Count == 0) return;

        foreach (var entry in affected)
        {
            if (Interlocked.CompareExchange(ref entry.Resolved, 1, 0) != 0) continue;
            await PersistAsync(entry, ApprovalStatus.Cancelled, "system");
            await SafeResolve(entry, false);
        }
        Changed?.Invoke();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var approval in await _persistence.LoadOpenAsync(ct))
        {
            var entry = new Entry(approval, _ => Task.CompletedTask);
            if (!_entries.TryAdd(approval.Id, entry)) continue;
            // Nobody is waiting on an interactive approval any more: its session died with the process.
            if (approval.Interactive && approval.Status == ApprovalStatus.Pending)
            {
                Interlocked.Exchange(ref entry.Resolved, 1);
                await PersistAsync(entry, ApprovalStatus.Cancelled, "system", ct);
            }
            else if (approval.Status != ApprovalStatus.Pending)
                Interlocked.Exchange(ref entry.Resolved, 1);
        }
        Changed?.Invoke();
    }

    private void SweepExpired()
    {
        var overdue = _entries.Values.Where(e => e.Approval.IsExpired).ToList();
        if (overdue.Count == 0) return;

        foreach (var entry in overdue)
        {
            if (Interlocked.CompareExchange(ref entry.Resolved, 1, 0) != 0) continue;
            _ = ExpireAsync(entry);
        }
        Changed?.Invoke();
    }

    private async Task ExpireAsync(Entry entry)
    {
        await PersistAsync(entry, ApprovalStatus.Expired, "system");
        await SafeResolve(entry, false); // unblock the waiting session; deny on timeout
    }

    /// <summary>Records the decision, then shows it. A human decision counts only once it is on record
    /// (<paramref name="mustStore"/>); a failed write of a system expiry or cancellation still ends it here (that only
    /// makes things safer: the record stays pending until it expires, and nothing can use a pending approval).</summary>
    private async Task<bool> PersistAsync(Entry entry, ApprovalStatus status, string? by, CancellationToken ct = default,
        bool mustStore = false)
    {
        var at = Now;
        DateTime? grantUntil = status == ApprovalStatus.Approved ? at + GrantLifetime : null;
        bool stored;
        try
        {
            stored = await _persistence.ResolveAsync(entry.Approval.Id, status, at, by, grantUntil, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Approval {Id} could not be recorded as {Status}", entry.Approval.Id, status);
            stored = false;
        }
        if (!stored && (mustStore || status == ApprovalStatus.Approved)) return false;
        entry.Approval.Status = status;
        entry.Approval.ResolvedAt = at;
        entry.Approval.ResolvedBy = by;
        entry.Approval.GrantExpiresAt = grantUntil;
        return true;
    }

    private async Task SafeResolve(Entry entry, bool approved)
    {
        try { await entry.Resolver(approved); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Approval resolver for {Tool} failed", entry.Approval.ToolName); }
    }

    /// <summary>Bounds memory: forgets the oldest approvals that can no longer be decided or used. An open approval
    /// — pending, or approved and not used yet — is never dropped, and the database keeps every one.</summary>
    private void TrimDecided()
    {
        var now = Now;
        var closed = _entries.Values
            .Where(e => e.Approval.Status != ApprovalStatus.Pending
                        && !(e.Approval.Status == ApprovalStatus.Approved && e.Approval.ConsumedAt is null
                             && e.Approval.GrantExpiresAt > now))
            .OrderBy(e => e.Approval.ResolvedAt ?? e.Approval.CreatedAt)
            .ToList();
        foreach (var e in closed.Take(Math.Max(0, closed.Count - MaxRetainedDecided)))
            _entries.TryRemove(e.Approval.Id, out _);
    }
}
