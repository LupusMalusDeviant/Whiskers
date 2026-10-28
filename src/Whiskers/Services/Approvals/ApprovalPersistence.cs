using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Approvals;

/// <summary>
/// Where approvals live (R1-14, INV-30/35): raised, decided and used approvals are written here before the store acts
/// on them, so a decision and a single use survive a restart. The state changes are conditional — a decision only
/// on a pending approval, a use only on an approved, unused one within its lifetime — so of any number of concurrent
/// consumers, in one process or across a restart, exactly one wins. Nothing is ever deleted.
/// </summary>
public interface IApprovalPersistence
{
    Task InsertAsync(Approval approval, CancellationToken ct = default);

    /// <summary>Pending → <paramref name="status"/>. <c>false</c> when the approval is not pending (any more), or —
    /// for a human decision (approved, rejected) — when it expired before <paramref name="at"/> (INV-32).
    /// <paramref name="stillDecidable"/> is asked for a human decision once the row is held (after any lock wait) and
    /// before the change is committed; <c>false</c> commits nothing — so a decision whose write waited past the
    /// expiry never becomes durable, not even for a moment.</summary>
    Task<bool> ResolveAsync(string approvalId, ApprovalStatus status, DateTime at, string? by, DateTime? grantExpiresAt,
        bool selfApproved = false, Func<bool>? stillDecidable = null, CancellationToken ct = default);

    /// <summary>Uses the approval once. <c>false</c> unless it is approved, unused and its grant has not lapsed at
    /// <paramref name="now"/>.</summary>
    Task<bool> ConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default);

    /// <summary>Approvals that can still be decided or used: pending ones, and approved ones not used yet.</summary>
    Task<IReadOnlyList<Approval>> LoadOpenAsync(CancellationToken ct = default);

    /// <summary>The history, newest first.</summary>
    Task<IReadOnlyList<Approval>> LoadHistoryAsync(int skip, int take, CancellationToken ct = default);
}

/// <summary>Approvals in the Whiskers database (table <c>Approvals</c>).</summary>
public sealed class DbApprovalPersistence : IApprovalPersistence
{
    private readonly IServiceScopeFactory _scopes;

    public DbApprovalPersistence(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    public async Task InsertAsync(Approval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Approvals.Add(ApprovalRecords.ToEntity(approval));
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ResolveAsync(string approvalId, ApprovalStatus status, DateTime at, string? by,
        DateTime? grantExpiresAt, bool selfApproved = false, Func<bool>? stillDecidable = null,
        CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var pending = nameof(ApprovalStatus.Pending);
        var decision = ApprovalRecords.IsDecision(status);
        // One transaction: the update holds the row (it waits here for any lock), then the decision is checked once
        // more against the clock before it is committed. Not committed (refused, or the process stops) = not decided.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Approvals
            .Where(a => a.Id == approvalId && a.Status == pending && (!decision || a.ExpiresAt > at))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status.ToString())
                .SetProperty(a => a.ResolvedAt, at)
                .SetProperty(a => a.ResolvedBy, by)
                .SetProperty(a => a.GrantExpiresAt, grantExpiresAt)
                .SetProperty(a => a.SelfApproved, selfApproved), ct);
        if (updated != 1) return false;
        if (decision && stillDecidable is not null && !stillDecidable()) return false;   // rolled back on dispose
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var approved = nameof(ApprovalStatus.Approved);
        var updated = await db.Approvals
            .Where(a => a.Id == approvalId && a.Status == approved && a.ConsumedAt == null
                        && a.GrantExpiresAt != null && a.GrantExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ConsumedAt, now)
                .SetProperty(a => a.ConsumedBy, operationId), ct);
        return updated == 1;
    }

    public async Task<IReadOnlyList<Approval>> LoadOpenAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var pending = nameof(ApprovalStatus.Pending);
        var approved = nameof(ApprovalStatus.Approved);
        var rows = await db.Approvals.AsNoTracking()
            .Where(a => a.Status == pending || (a.Status == approved && a.ConsumedAt == null))
            .ToListAsync(ct);
        return rows.Select(ApprovalRecords.FromEntity).ToList();
    }

    public async Task<IReadOnlyList<Approval>> LoadHistoryAsync(int skip, int take, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var rows = await db.Approvals.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Max(0, take))
            .ToListAsync(ct);
        return rows.Select(ApprovalRecords.FromEntity).ToList();
    }
}

/// <summary>Approvals in memory only: for a host without a database (unit tests). Same conditional state changes.</summary>
public sealed class InMemoryApprovalPersistence : IApprovalPersistence
{
    private readonly Dictionary<string, ApprovalRecordEntity> _rows = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Task InsertAsync(Approval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_lock)
        {
            if (!_rows.TryAdd(approval.Id, ApprovalRecords.ToEntity(approval)))
                throw new InvalidOperationException($"Approval {approval.Id} exists already.");
        }
        return Task.CompletedTask;
    }

    public Task<bool> ResolveAsync(string approvalId, ApprovalStatus status, DateTime at, string? by,
        DateTime? grantExpiresAt, bool selfApproved = false, Func<bool>? stillDecidable = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_rows.TryGetValue(approvalId, out var row) || row.Status != nameof(ApprovalStatus.Pending)
                || (ApprovalRecords.IsDecision(status) && row.ExpiresAt <= at))
                return Task.FromResult(false);
            if (ApprovalRecords.IsDecision(status) && stillDecidable is not null && !stillDecidable())
                return Task.FromResult(false);
            row.Status = status.ToString();
            row.ResolvedAt = at;
            row.ResolvedBy = by;
            row.GrantExpiresAt = grantExpiresAt;
            row.SelfApproved = selfApproved;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_rows.TryGetValue(approvalId, out var row) || row.Status != nameof(ApprovalStatus.Approved)
                || row.ConsumedAt is not null || row.GrantExpiresAt is not { } until || until <= now)
                return Task.FromResult(false);
            row.ConsumedAt = now;
            row.ConsumedBy = operationId;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Approval>> LoadOpenAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Approval>>(_rows.Values
                .Where(a => a.Status == nameof(ApprovalStatus.Pending)
                            || (a.Status == nameof(ApprovalStatus.Approved) && a.ConsumedAt is null))
                .Select(ApprovalRecords.FromEntity).ToList());
    }

    public Task<IReadOnlyList<Approval>> LoadHistoryAsync(int skip, int take, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Approval>>(_rows.Values
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Skip(Math.Max(0, skip)).Take(Math.Max(0, take))
                .Select(ApprovalRecords.FromEntity).ToList());
    }
}

internal static class ApprovalRecords
{
    /// <summary>A human decision, as opposed to the system expiring or cancelling an approval.</summary>
    public static bool IsDecision(ApprovalStatus status) => status is ApprovalStatus.Approved or ApprovalStatus.Rejected;

    // Principal ids never contain a line break (user:, key:, trigger:, agent:, task: ids).
    private const char RequesterSeparator = '\n';

    private static string? JoinRequesters(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? null : string.Join(RequesterSeparator, ids);

    private static IReadOnlyList<string> SplitRequesters(string? value) =>
        string.IsNullOrEmpty(value)
            ? Array.Empty<string>()
            : value.Split(RequesterSeparator, StringSplitOptions.RemoveEmptyEntries);

    public static ApprovalRecordEntity ToEntity(Approval a) => new()
    {
        Id = a.Id,
        SessionId = a.SessionId,
        ToolCallId = a.ToolCallId,
        Actor = a.Actor,
        ActorType = a.ActorType,
        ToolName = a.ToolName,
        Level = a.Level,
        ParamsJson = a.ParamsJson,
        Reason = a.Reason,
        Diff = a.Diff,
        CorrelationId = a.CorrelationId,
        RiskLevel = a.RiskLevel,
        TargetServer = a.TargetServer,
        TargetWorkload = a.TargetWorkload,
        GuardrailPreset = a.GuardrailPreset,
        GuardrailRule = a.GuardrailRule,
        ArgumentsHash = a.ArgumentsHash,
        DecisionId = a.DecisionId,
        Binding = a.Binding,
        Interactive = a.Interactive,
        RequesterIds = JoinRequesters(a.Requesters),
        SelfApproved = a.SelfApproved,
        Status = a.Status.ToString(),
        CreatedAt = a.CreatedAt,
        ExpiresAt = a.ExpiresAt,
        ResolvedAt = a.ResolvedAt,
        ResolvedBy = a.ResolvedBy,
        GrantExpiresAt = a.GrantExpiresAt,
        ConsumedAt = a.ConsumedAt,
        ConsumedBy = a.ConsumedBy,
    };

    public static Approval FromEntity(ApprovalRecordEntity e) => new()
    {
        Id = e.Id,
        SessionId = e.SessionId,
        ToolCallId = e.ToolCallId,
        Actor = e.Actor,
        ActorType = e.ActorType,
        ToolName = e.ToolName,
        Level = e.Level,
        ParamsJson = e.ParamsJson,
        Reason = e.Reason,
        Diff = e.Diff,
        CorrelationId = e.CorrelationId,
        RiskLevel = e.RiskLevel,
        TargetServer = e.TargetServer,
        TargetWorkload = e.TargetWorkload,
        GuardrailPreset = e.GuardrailPreset,
        GuardrailRule = e.GuardrailRule,
        ArgumentsHash = e.ArgumentsHash,
        DecisionId = e.DecisionId,
        Binding = e.Binding,
        Interactive = e.Interactive,
        Requesters = SplitRequesters(e.RequesterIds),
        SelfApproved = e.SelfApproved,
        // An unreadable status is never a usable one.
        Status = Enum.TryParse<ApprovalStatus>(e.Status, out var status) ? status : ApprovalStatus.Cancelled,
        CreatedAt = DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc),
        ExpiresAt = DateTime.SpecifyKind(e.ExpiresAt, DateTimeKind.Utc),
        ResolvedAt = Utc(e.ResolvedAt),
        ResolvedBy = e.ResolvedBy,
        GrantExpiresAt = Utc(e.GrantExpiresAt),
        ConsumedAt = Utc(e.ConsumedAt),
        ConsumedBy = e.ConsumedBy,
    };

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
