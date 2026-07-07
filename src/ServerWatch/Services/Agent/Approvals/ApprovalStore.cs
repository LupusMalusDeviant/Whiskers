using System.Collections.Concurrent;
using ServerWatch.Models;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Approvals;

/// <summary>Central registry of Human-in-the-Loop approvals. When an agent tool call needs a human
/// decision (guardrail <c>Confirm</c>), the flow creates an approval here with a <paramref name="resolver"/>
/// that bridges back to the live <see cref="IAgentSession"/>. The "Freigaben" page and the notification
/// bell read pending approvals and call <see cref="ResolveAsync"/>.
/// In-memory by design: the resolver targets a running session, which itself doesn't survive a restart.</summary>
public interface IApprovalStore
{
    /// <summary>Registers a pending approval. <paramref name="resolver"/> is invoked with the human's
    /// decision (true = approve) exactly once when the approval is resolved.</summary>
    Approval Create(ApprovalRequest request, Func<bool, Task> resolver);

    /// <summary>Pending approvals (newest first), with overdue ones swept to Expired first.</summary>
    IReadOnlyList<Approval> GetPending();

    /// <summary>All approvals (pending + recently resolved), newest first.</summary>
    IReadOnlyList<Approval> GetAll();

    Approval? Get(string approvalId);

    /// <summary>Approve/reject a pending approval. Invokes its resolver. Returns false if not pending.
    /// When <paramref name="resolverLevel"/> is supplied (read/write/admin), the resolver must hold at
    /// least the approval's own level — a lower-privileged user cannot approve a higher-privileged action.
    /// Pass null only for system-internal resolves (expiry/cancel).</summary>
    Task<bool> ResolveAsync(string approvalId, bool approved, string? resolvedBy, string? resolverLevel = null);

    /// <summary>Cancels any pending approvals for a session that ended/aborted (resolver gets false).</summary>
    Task CancelForSessionAsync(string sessionId);

    /// <summary>Raised whenever the pending set changes (create/resolve/expire) — drives bell + page.</summary>
    event Action? Changed;
}

public sealed class ApprovalStore : IApprovalStore
{
    private const int MaxRetained = 200;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ILogger<ApprovalStore>? _logger;

    public ApprovalStore(ILogger<ApprovalStore>? logger = null) => _logger = logger;

    public event Action? Changed;

    private sealed record Entry(Approval Approval, Func<bool, Task> Resolver);

    public Approval Create(ApprovalRequest request, Func<bool, Task> resolver)
    {
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
        };
        _entries[approval.Id] = new Entry(approval, resolver);
        TrimResolved();
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

    public Approval? Get(string approvalId) =>
        _entries.TryGetValue(approvalId, out var e) ? e.Approval : null;

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

        Mark(entry.Approval, approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected, resolvedBy);
        await SafeResolve(entry, approved);
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
            Mark(entry.Approval, ApprovalStatus.Cancelled, "system");
            await SafeResolve(entry, false);
        }
        Changed?.Invoke();
    }

    private void SweepExpired()
    {
        var overdue = _entries.Values.Where(e => e.Approval.IsExpired).ToList();
        if (overdue.Count == 0) return;

        foreach (var entry in overdue)
        {
            Mark(entry.Approval, ApprovalStatus.Expired, "system");
            _ = SafeResolve(entry, false); // unblock the waiting session; deny on timeout
        }
        Changed?.Invoke();
    }

    private static void Mark(Approval a, ApprovalStatus status, string? by)
    {
        a.Status = status;
        a.ResolvedAt = DateTime.UtcNow;
        a.ResolvedBy = by;
    }

    private async Task SafeResolve(Entry entry, bool approved)
    {
        try { await entry.Resolver(approved); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Approval resolver for {Tool} failed", entry.Approval.ToolName); }
    }

    /// <summary>Bounds memory: drop the oldest resolved entries once the map grows past the cap.</summary>
    private void TrimResolved()
    {
        if (_entries.Count <= MaxRetained) return;
        var resolved = _entries.Values
            .Where(e => e.Approval.Status != ApprovalStatus.Pending)
            .OrderBy(e => e.Approval.ResolvedAt ?? e.Approval.CreatedAt)
            .Take(_entries.Count - MaxRetained)
            .ToList();
        foreach (var e in resolved) _entries.TryRemove(e.Approval.Id, out _);
    }
}
