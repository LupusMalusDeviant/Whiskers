using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>An outcome the gateway could not write to its intent row; kept and written again.</summary>
public sealed record PendingAuditOutcome(long AuditId, string OperationId, MutationStatus Status,
    VerificationStatus Verification, string? Reason, TimeSpan Duration);

/// <summary>A refusal the gateway could not record; kept and recorded again.</summary>
public sealed record PendingAuditRefusal(string OperationId, MutationRequest Request, Capability? Capability,
    MutationStatus Status, string Reason, string? ApprovalId);

/// <summary>Whether the audit trail is complete right now (R1-19, INV-45).</summary>
/// <param name="PendingOutcomes">Outcomes and refusals of this process still waiting to be written.</param>
/// <param name="UnsettledIntents">Intent rows the last recovery pass could not settle (or the pass itself failed).</param>
/// <param name="LegacyWriteFailed">A write of the older, non-gateway audit failed in the last hour.</param>
public sealed record AuditHealth(int PendingOutcomes, int UnsettledIntents, bool LegacyWriteFailed)
{
    public bool Complete => PendingOutcomes == 0 && UnsettledIntents == 0 && !LegacyWriteFailed;
}

/// <summary>
/// Keeps the audit trail recoverable when its outcome write fails (R1-19). The intent row is already durable — the
/// mutation ran — so a failed outcome write is kept in memory and written again, and after a restart the intent rows
/// still in <c>intent</c> are settled from the operation journal (R1-18): executed or failed as the journal recorded
/// it, <c>Unknown</c> when it was still running when Whiskers stopped, denied when it never ran. Until the trail is
/// complete again <see cref="Health"/> says so (the <c>audit</c> readiness check reports Degraded).
/// </summary>
public interface IAuditRecovery
{
    void OutcomeNotWritten(PendingAuditOutcome outcome);
    void RefusalNotWritten(PendingAuditRefusal refusal);

    /// <summary>The older audit (<c>AuditLogService</c>, written after the fact) lost a row.</summary>
    void LegacyWriteFailed();

    AuditHealth Health { get; }

    /// <summary>One pass: writes the kept outcomes and refusals, then settles stale intent rows.</summary>
    Task RecoverAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAuditRecovery"/>
public sealed class AuditRecovery : FleetBackgroundService, IAuditRecovery
{
    /// <summary>How long an intent may wait for its outcome before a pass looks at it: an intent row is written right
    /// before the operation is claimed, so an intent without a journal row this old never ran.</summary>
    internal static readonly TimeSpan IntentGrace = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxKept = 10_000;
    private const int BatchSize = 500;

    private readonly IMutationAuditSink _sink;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOperationJournal? _journal;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditRecovery> _logger;
    private readonly ConcurrentDictionary<long, PendingAuditOutcome> _outcomes = new();
    private readonly ConcurrentDictionary<Guid, PendingAuditRefusal> _refusals = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private int _unsettled;
    private int _dropped;
    private long _legacyFailedAt;   // UTC ticks; 0 = never

    public AuditRecovery(IMutationAuditSink sink, IServiceScopeFactory scopes, ILogger<AuditRecovery> logger,
        IOperationJournal? journal = null, TimeProvider? time = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _journal = journal;
        _time = time ?? TimeProvider.System;
    }

    public void OutcomeNotWritten(PendingAuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (_outcomes.Count + _refusals.Count >= MaxKept) { Interlocked.Increment(ref _dropped); return; }
        _outcomes[outcome.AuditId] = outcome;
    }

    public void RefusalNotWritten(PendingAuditRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        if (_outcomes.Count + _refusals.Count >= MaxKept) { Interlocked.Increment(ref _dropped); return; }
        _refusals[Guid.NewGuid()] = refusal;
    }

    public void LegacyWriteFailed() => Interlocked.Exchange(ref _legacyFailedAt, _time.GetUtcNow().UtcTicks);

    public AuditHealth Health => new(
        _outcomes.Count + _refusals.Count + Volatile.Read(ref _dropped),
        Volatile.Read(ref _unsettled),
        Interlocked.Read(ref _legacyFailedAt) is var failed and not 0
            && _time.GetUtcNow().UtcTicks - failed < TimeSpan.TicksPerHour);

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database may still be migrating at startup, or be gone: the next pass tries again.
                _logger.LogWarning(ex, "Audit recovery pass failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await _pass.WaitAsync(ct);
        try
        {
            foreach (var (id, outcome) in _outcomes)
            {
                try
                {
                    await _sink.CompleteAsync(outcome.AuditId, outcome.OperationId, outcome.Status, outcome.Verification,
                        outcome.Reason, outcome.Duration, ct);
                    _outcomes.TryRemove(id, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Audit outcome of operation {OperationId} still not written", outcome.OperationId);
                }
            }
            foreach (var (id, refusal) in _refusals)
            {
                try
                {
                    await _sink.RecordDeniedAsync(refusal.OperationId, refusal.Request, refusal.Capability, refusal.Status,
                        refusal.Reason, refusal.ApprovalId, ct);
                    _refusals.TryRemove(id, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Audit refusal of operation {OperationId} still not recorded", refusal.OperationId);
                }
            }
            try
            {
                Volatile.Write(ref _unsettled, await SettleIntentsAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Unknown how many: the trail is not known to be complete.
                Volatile.Write(ref _unsettled, Math.Max(1, Volatile.Read(ref _unsettled)));
                throw;
            }
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>Settles intent rows older than <see cref="IntentGrace"/> from the journal; returns how many are left
    /// that should have an outcome by now.</summary>
    private async Task<int> SettleIntentsAsync(CancellationToken ct)
    {
        // Without a journal nothing tells a running operation from one that was cut off: leave them.
        if (_journal is null) return 0;
        var self = (_journal as DbOperationJournal)?.ProcessInstance;
        var cutoff = _time.GetUtcNow().UtcDateTime - IntentGrace;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var left = 0;
        long after = 0;
        while (true)
        {
            var rows = await db.AuditLog
                .Where(a => a.Phase == "intent" && a.Timestamp < cutoff && a.Id > after)
                .OrderBy(a => a.Id).Take(BatchSize).ToListAsync(ct);
            if (rows.Count == 0) return left;
            after = rows[^1].Id;
            var ids = rows.Select(r => r.Id).ToList();
            var operations = await db.Operations.AsNoTracking()
                .Where(o => o.AuditId != null && ids.Contains(o.AuditId.Value))
                .ToDictionaryAsync(o => o.AuditId!.Value, ct);
            foreach (var row in rows)
            {
                // Kept in memory with its exact outcome: written by the first half of the pass.
                if (_outcomes.ContainsKey(row.Id)) continue;
                operations.TryGetValue(row.Id, out var operation);
                var operationId = row.OperationId ?? operation?.Id ?? "";
                switch (operation?.State)
                {
                    case null or DbOperationJournal.Released:
                        // Never claimed, or given back: it did not run.
                        AuditLogMutationSink.Complete(row, operationId, nameof(MutationStatus.Denied),
                            nameof(VerificationStatus.NotRun), "Recovered: the operation did not run.", 0);
                        break;
                    case DbOperationJournal.Running when self is not null
                                                         && operation.Instance.StartsWith(self + "/", StringComparison.Ordinal):
                        // Running in this process right now.
                        continue;
                    case DbOperationJournal.Running:
                        AuditLogMutationSink.Complete(row, operationId, "Unknown", nameof(VerificationStatus.NotRun),
                            "Recovered: Whiskers stopped while it ran; whether it took effect is unknown.", null);
                        break;
                    default:
                        AuditLogMutationSink.Complete(row, operationId, operation.State,
                            operation.Verification ?? nameof(VerificationStatus.NotRun),
                            "Recovered from the operation journal; the original outcome text was not recorded.",
                            operation.FinishedAt is { } finished
                                ? (long)(finished - operation.ClaimedAt).TotalMilliseconds
                                : null);
                        break;
                }
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Audit intents could not be settled");
                db.ChangeTracker.Clear();
                left += rows.Count;
            }
        }
    }
}

