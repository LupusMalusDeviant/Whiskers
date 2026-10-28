using System.Collections.Concurrent;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;

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
    /// an approval nobody could find again is never raised. Throws <see cref="ApprovalLimitException"/> when its
    /// requesting principal already has <see cref="ApprovalEligibility.MaxPendingPerPrincipal"/> approvals waiting.</summary>
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

    /// <summary>A human approves or rejects a pending approval (R1-15). Both are held to the same rules
    /// (<see cref="ApprovalEligibility"/>): the resolver holds at least the approval's level, and a requester never
    /// decides a non-interactive request — nor an interactive one while the policy requires a second approver. An
    /// approval past its expiry is expired instead. Invokes its resolver once it is on record.</summary>
    Task<ApprovalDecisionResult> DecideAsync(string approvalId, bool approved, ApprovalResolver resolver);

    /// <summary><see cref="DecideAsync"/> for the web user <paramref name="resolvedBy"/> (an email) holding
    /// <paramref name="resolverLevel"/>; <c>true</c> when it was decided. Without both, nothing is decided.</summary>
    Task<bool> ResolveAsync(string approvalId, bool approved, string? resolvedBy, string? resolverLevel = null);

    /// <summary>Whether <paramref name="resolver"/> could decide <paramref name="approvalId"/> now; <c>null</c> when
    /// they could, otherwise why not. For the UI: <see cref="DecideAsync"/> checks again.</summary>
    ApprovalDecisionResult? CanDecide(string approvalId, ApprovalResolver resolver);

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

public sealed class ApprovalStore : IApprovalStore, IDisposable
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
    private readonly IGuardrailStore? _policy;
    private readonly ITimer _sweep;

    // Counting a principal's pending approvals and adding one happen together, so the limit holds under concurrency.
    private readonly SemaphoreSlim _createLock = new(1, 1);

    /// <summary>How often overdue approvals are expired even when nobody looks at the list, so a session waiting on
    /// one is released (C-07).</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    public ApprovalStore(ILogger<ApprovalStore>? logger = null, TimeSpan? approvalTtl = null,
        IApprovalPersistence? persistence = null, TimeProvider? time = null, IGuardrailStore? policy = null)
    {
        _logger = logger;
        _ttl = approvalTtl ?? TimeSpan.FromMinutes(15);
        _persistence = persistence ?? new InMemoryApprovalPersistence();
        _time = time ?? TimeProvider.System;
        _policy = policy;
        _sweep = _time.CreateTimer(_ => SweepExpired(), null, SweepInterval, SweepInterval);
    }

    public void Dispose() => _sweep.Dispose();

    /// <summary>Whether the active policy requires a second approver for interactive requests too. Unreadable →
    /// required (fail closed).</summary>
    private bool RequireSecondApprover
    {
        get
        {
            if (_policy is null) return false;
            try { return _policy.Current.RequireSecondApprover; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Guardrail policy could not be read; a second approver is required");
                return true;
            }
        }
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
        await _createLock.WaitAsync(ct);
        try
        {
            return await CreateLockedAsync(request, resolver, ct);
        }
        finally
        {
            _createLock.Release();
        }
    }

    private async Task<Approval> CreateLockedAsync(ApprovalRequest request, Func<bool, Task> resolver, CancellationToken ct)
    {
        var requesters = (request.Requesters ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        // C-19: one principal cannot flood the list (and the bell). Counted against the requesting principal, or the
        // displayed actor for a caller that names none.
        var counted = requesters.Count > 0 ? requesters[0] : request.Actor;
        SweepExpired();
        if (_entries.Values.Count(e => e.Approval.Status == ApprovalStatus.Pending
                                       && RequesterOf(e.Approval) == counted) >= ApprovalEligibility.MaxPendingPerPrincipal)
        {
            _logger?.LogWarning("Approval for {Tool} not raised: {Requester} has too many pending approvals",
                request.ToolName, counted);
            throw new ApprovalLimitException(counted);
        }

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
            Requesters = requesters,
        };
        // Persisted first: an approval that is not on record is never shown, decided or used.
        await _persistence.InsertAsync(approval, ct);
        _entries[approval.Id] = new Entry(approval, resolver);
        TrimDecided();
        Changed?.Invoke();
        return approval;
    }

    private static string RequesterOf(Approval approval) =>
        approval.Requesters.Count > 0 ? approval.Requesters[0] : approval.Actor;

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
        // A decision always names who made it and at which level; without them nothing is decided (R1-15).
        if (string.IsNullOrWhiteSpace(resolvedBy) || string.IsNullOrWhiteSpace(resolverLevel))
        {
            _logger?.LogWarning("Approval {Id} not decided: no resolver or no resolver level was given", approvalId);
            return false;
        }
        var resolver = new ApprovalResolver(Mutations.PrincipalIds.User(resolvedBy), resolverLevel, resolvedBy.Trim());
        return await DecideAsync(approvalId, approved, resolver) == ApprovalDecisionResult.Decided;
    }

    public ApprovalDecisionResult? CanDecide(string approvalId, ApprovalResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (!_entries.TryGetValue(approvalId, out var entry)) return ApprovalDecisionResult.NotFound;
        if (entry.Approval.IsExpiredAt(Now)) return ApprovalDecisionResult.Expired;
        if (entry.Approval.Status != ApprovalStatus.Pending) return ApprovalDecisionResult.NotPending;
        return ApprovalEligibility.Check(entry.Approval, resolver, RequireSecondApprover);
    }

    public async Task<ApprovalDecisionResult> DecideAsync(string approvalId, bool approved, ApprovalResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (!_entries.TryGetValue(approvalId, out var entry)) return ApprovalDecisionResult.NotFound;
        var approval = entry.Approval;

        // INV-32: past its expiry it is expired, not decided — whoever looks first.
        if (approval.IsExpiredAt(Now))
        {
            SweepExpired();
            return ApprovalDecisionResult.Expired;
        }
        if (approval.Status != ApprovalStatus.Pending) return ApprovalDecisionResult.NotPending;

        // INV-33/34/37: approving and rejecting are held to the same rules.
        if (ApprovalEligibility.Check(approval, resolver, RequireSecondApprover) is { } refused)
        {
            _logger?.LogWarning("Approval {Id} ({Tool}, level={Level}) not {Decision} by {Resolver} (level {ResolverLevel}): {Reason}",
                approvalId, approval.ToolName, approval.Level, approved ? "approved" : "rejected", resolver.PrincipalId,
                resolver.Level, refused);
            return refused;
        }
        var selfApproved = ApprovalEligibility.IsRequester(approval, resolver);

        // Exactly-once: only the thread that flips Resolved 0→1 performs the transition + resolver.
        if (Interlocked.CompareExchange(ref entry.Resolved, 1, 0) != 0)
            return ApprovalDecisionResult.NotPending;

        var status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        if (!await PersistAsync(entry, status, resolver.Display, mustStore: true, selfApproved: selfApproved))
        {
            // Not on record (decided elsewhere first, expired meanwhile, or the write failed), not decided here: the
            // approval stays pending in this store and nothing waiting on it is released.
            Interlocked.Exchange(ref entry.Resolved, 0);
            if (approval.IsExpiredAt(Now))
            {
                SweepExpired();
                return ApprovalDecisionResult.Expired;
            }
            return ApprovalDecisionResult.NotRecorded;
        }
        if (selfApproved)
            _logger?.LogInformation("Approval {Id} ({Tool}) was {Decision} by its own requester {Resolver}",
                approvalId, approval.ToolName, approved ? "approved" : "rejected", resolver.PrincipalId);
        await SafeResolve(entry, approved);
        Changed?.Invoke();
        return ApprovalDecisionResult.Decided;
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
            // Nobody is waiting on an interactive approval any more: its session died with the process. And a pending
            // approval that does not say who asked (raised before R1-15) can never be decided: it is cancelled, the
            // caller asks again.
            if (approval.Status == ApprovalStatus.Pending && (approval.Interactive || approval.Requesters.Count == 0))
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
        var now = Now;
        var overdue = _entries.Values.Where(e => e.Approval.IsExpiredAt(now)).ToList();
        if (overdue.Count == 0) return;

        foreach (var entry in overdue)
        {
            if (Interlocked.CompareExchange(ref entry.Resolved, 1, 0) != 0) continue;
            // Expired here at once, so nothing reads it as pending while the record is written.
            entry.Approval.Status = ApprovalStatus.Expired;
            entry.Approval.ResolvedAt = now;
            entry.Approval.ResolvedBy = "system";
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
        bool mustStore = false, bool selfApproved = false)
    {
        var at = Now;
        DateTime? grantUntil = status == ApprovalStatus.Approved ? at + GrantLifetime : null;
        bool stored;
        try
        {
            stored = await _persistence.ResolveAsync(entry.Approval.Id, status, at, by, grantUntil, selfApproved, ct);
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
        entry.Approval.SelfApproved = selfApproved;
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
