using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>An outcome the gateway could not write to its intent row; kept and written again.</summary>
public sealed record PendingAuditOutcome(long AuditId, string OperationId, MutationStatus Status,
    VerificationStatus Verification, string? Reason, TimeSpan Duration);

/// <summary>Whether the audit trail is complete right now (R1-19, INV-45).</summary>
/// <param name="PendingEvents">Outcomes of this process and refusals in the outbox still waiting to be written, and
/// events lost altogether (neither written nor kept).</param>
/// <param name="UnsettledIntents">Intent rows without their outcome that are not running in this process — or 1 while
/// no recovery pass has completed yet (unknown is not complete).</param>
/// <param name="LegacyWriteFailed">A write of the older, non-gateway audit failed in the last hour.</param>
public sealed record AuditHealth(int PendingEvents, int UnsettledIntents, bool LegacyWriteFailed)
{
    public bool Complete => PendingEvents == 0 && UnsettledIntents == 0 && !LegacyWriteFailed;
}

/// <summary>
/// Keeps the audit trail recoverable when a write fails (R1-19). An outcome's intent row is already durable — the
/// mutation ran — so a failed outcome write is kept in memory and written again; a refusal, which has no intent row,
/// is kept in the <see cref="AuditOutbox"/> by the sink and inserted from there (idempotent by its event id). After a
/// restart the intent rows still in <c>intent</c> are settled from the operation journal (R1-18): executed or failed
/// as the journal recorded it, <c>Unknown</c> when it was still running when Whiskers stopped, denied when it never
/// ran. Until the trail is complete again <see cref="Health"/> says so (the <c>audit</c> readiness check reports
/// Degraded).
/// </summary>
public interface IAuditRecovery
{
    void OutcomeNotWritten(PendingAuditOutcome outcome);

    /// <summary>The older audit (<c>AuditLogService</c>, written after the fact) lost a row.</summary>
    void LegacyWriteFailed();

    AuditHealth Health { get; }

    /// <summary>One pass: writes the kept outcomes and the outbox, then settles intent rows left without an outcome.</summary>
    Task RecoverAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAuditRecovery"/>
public sealed class AuditRecovery : FleetBackgroundService, IAuditRecovery
{
    /// <summary>How long an intent without a journal row may wait: the intent is written right before the operation
    /// is claimed, so one without a claim this old never ran. An intent whose claim is finished, or was cut off by a
    /// restart, is settled at once.</summary>
    internal static readonly TimeSpan IntentGrace = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxKept = 10_000;
    private const int BatchSize = 500;

    private readonly IMutationAuditSink _sink;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOperationJournal? _journal;
    private readonly AuditOutbox? _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditRecovery> _logger;
    private readonly ConcurrentDictionary<long, PendingAuditOutcome> _outcomes = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    // Unknown until the first pass has looked: not complete.
    private int _unsettled = 1;
    private long _legacyFailedAt;   // UTC ticks; 0 = never

    public AuditRecovery(IMutationAuditSink sink, IServiceScopeFactory scopes, ILogger<AuditRecovery> logger,
        IOperationJournal? journal = null, TimeProvider? time = null, AuditOutbox? outbox = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _journal = journal;
        _outbox = outbox;
        _time = time ?? TimeProvider.System;
    }

    public void OutcomeNotWritten(PendingAuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        // Beyond the cap the outcome is not kept in memory: its intent row stays, counts as unsettled and is settled
        // from the journal by the next pass.
        if (_outcomes.Count >= MaxKept) return;
        _outcomes[outcome.AuditId] = outcome;
    }

    public void LegacyWriteFailed() => Interlocked.Exchange(ref _legacyFailedAt, _time.GetUtcNow().UtcTicks);

    public AuditHealth Health => new(
        _outcomes.Count + (_outbox?.Count ?? 0) + (_outbox?.Lost ?? 0),
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
            await DrainOutboxAsync(ct);
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

    /// <summary>Inserts the refusals kept in the outbox; one already in the table (same event id) is just removed.</summary>
    private async Task DrainOutboxAsync(CancellationToken ct)
    {
        if (_outbox is null) return;
        foreach (var (path, row) in _outbox.Load())
        {
            if (row is null) continue;   // unreadable: stays, and counts as pending
            try
            {
                row.Id = 0;
                using var scope = _scopes.CreateScope();
                await AuditLogMutationSink.InsertOnceAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), row, ct);
                _outbox.Remove(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Audit event {EventId} from the outbox still not written", row.EventId);
            }
        }
    }

    /// <summary>Settles the intent rows left without an outcome from the journal; returns how many are left that are
    /// not running in this process.</summary>
    private async Task<int> SettleIntentsAsync(CancellationToken ct)
    {
        // Without a journal nothing tells a running operation from one that was cut off: leave them.
        if (_journal is null) return 0;
        var self = (_journal as DbOperationJournal)?.ProcessInstance;
        // The real sink knows the intents this process is writing right now; any other sink gets the grace period.
        var sink = _sink as AuditLogMutationSink;
        var cutoff = _time.GetUtcNow().UtcDateTime - IntentGrace;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var left = 0;
        long after = 0;
        while (true)
        {
            var rows = await db.AuditLog.AsNoTracking()
                .Where(a => a.Phase == "intent" && a.Id > after)
                .OrderBy(a => a.Id).Take(BatchSize).ToListAsync(ct);
            if (rows.Count == 0) return left;
            after = rows[^1].Id;
            var ids = rows.Select(r => r.Id).ToList();
            var operations = await db.Operations.AsNoTracking()
                .Where(o => o.AuditId != null && ids.Contains(o.AuditId.Value))
                .ToDictionaryAsync(o => o.AuditId!.Value, ct);
            foreach (var row in rows)
            {
                // Kept in memory with its exact outcome: written by the first half of the pass, counted as pending.
                if (_outcomes.ContainsKey(row.Id)) continue;
                // This process is running it right now (between its intent and its outcome).
                if (sink?.InFlight(row.EventId) == true) continue;
                operations.TryGetValue(row.Id, out var operation);
                var operationId = row.OperationId ?? operation?.Id ?? "";
                switch (operation?.State)
                {
                    case null or DbOperationJournal.Released when sink is null && row.Timestamp >= cutoff:
                        // Possibly between its intent and its claim right now: not settled yet, but not complete.
                        left++;
                        continue;
                    case null or DbOperationJournal.Released:
                        // Never claimed, or given back: it did not run.
                        AuditLogMutationSink.Complete(row, operationId, nameof(MutationStatus.Denied),
                            nameof(VerificationStatus.NotRun), "Recovered: the operation did not run.", 0);
                        break;
                    case DbOperationJournal.Running when self is not null
                                                         && operation.Instance.StartsWith(self + "/", StringComparison.Ordinal):
                        // Claimed by this process. Still running (the real sink would know), or its journal entry
                        // could not be finished — then its outcome is not known here: incomplete, visibly.
                        if (sink is not null) left++;
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
                // Only while it is still the intent: the gateway may have completed it since this pass read it, and
                // its outcome is never overwritten.
                try
                {
                    await db.AuditLog.Where(a => a.Id == row.Id && a.Phase == "intent")
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.Phase, row.Phase)
                            .SetProperty(a => a.Outcome, row.Outcome)
                            .SetProperty(a => a.Verification, row.Verification)
                            .SetProperty(a => a.DurationMs, row.DurationMs)
                            .SetProperty(a => a.Success, row.Success)
                            .SetProperty(a => a.OperationId, row.OperationId)
                            .SetProperty(a => a.Details, row.Details), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Audit intent {AuditId} could not be settled", row.Id);
                    left++;
                }
            }
        }
    }
}
