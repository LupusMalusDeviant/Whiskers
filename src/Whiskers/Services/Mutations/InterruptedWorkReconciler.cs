using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>
/// R1-39 (F-16): at start, what an earlier process left unfinished — because it was stopped past the drain budget,
/// killed or crashed — is marked as interrupted, never repeated:
/// <list type="bullet">
/// <item>operations still <c>Running</c> under another process become <c>Interrupted</c> in the journal: a retry under
/// their id stays refused as "outcome unknown", and the audit recovery settles their intent as <c>Unknown</c>;</item>
/// <item>scheduled-task runs without an end become failed runs that say they were interrupted — the history tells an
/// interruption apart from a success and from an ordinary failure — and their task shows that as its last result;</item>
/// <item>AutoUpdate attempts without an end become failed updates that say they were interrupted, and the container's
/// update policy is switched off: the same update is not tried again until an admin has checked the container and
/// switches AutoUpdate on again.</item>
/// </list>
/// AutoUpdate waits for this pass (<see cref="IStartupReconciliation"/>) before its first check.
/// One notification names them all, so an admin checks the targets: whether an interrupted update, backup or command
/// took effect is unknown.
/// </summary>
public sealed class InterruptedWorkReconciler : FleetBackgroundService, IStartupReconciliation
{
    /// <summary>The result text of an interrupted task run (also how the history and the task page recognise one).</summary>
    public const string InterruptedResult =
        "INTERRUPTED: Whiskers stopped while this ran; whether it took effect is unknown. It was not run again.";

    /// <summary>The policy result of an AutoUpdate that was cut off (R1-39).</summary>
    public const string InterruptedUpdateResult =
        "INTERRUPTED: Whiskers stopped while this container was being updated; whether the update took effect is " +
        "unknown. AutoUpdate was switched off for it — check the container, then switch it on again.";

    private const int MaxNamed = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly IOperationJournal _journal;
    private readonly INotificationService _notifications;
    private readonly ILogger<InterruptedWorkReconciler> _logger;
    private readonly DateTime _processStart;

    public InterruptedWorkReconciler(IServiceScopeFactory scopes, IOperationJournal journal,
        INotificationService notifications, ILogger<InterruptedWorkReconciler> logger)
        : this(scopes, journal, notifications, logger,
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()) { }

    internal InterruptedWorkReconciler(IServiceScopeFactory scopes, IOperationJournal journal,
        INotificationService notifications, ILogger<InterruptedWorkReconciler> logger, DateTime processStartUtc)
    {
        _scopes = scopes;
        _journal = journal;
        _notifications = notifications;
        _logger = logger;
        _processStart = processStartUtc;
    }

    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completed => _completed.Task;

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // What was marked before the failure is reported already (ReconcileAsync); what was not stays open and is
            // marked by the next start. An open AutoUpdate attempt keeps its container from being updated meanwhile.
            _logger.LogError(ex, "Interrupted work of an earlier run could not be reconciled");
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    /// <summary>One pass; returns what it marked. Internal for tests.</summary>
    internal async Task<IReadOnlyList<string>> ReconcileAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var now = DateTime.UtcNow;
        var marked = new List<string>();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        try
        {
            await MarkAsync(db, now, marked, ct);
        }
        catch (Exception ex)
        {
            // Every mark is committed on its own: those made before the failure are reported now — the next start no
            // longer sees them — and the failure is raised after the report.
            failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }
        await ReportAsync(marked);
        failure?.Throw();
        return marked;
    }

    private async Task MarkAsync(MetricsDbContext db, DateTime now, List<string> marked, CancellationToken ct)
    {
        // Operations: only claims of another process. A claim of this one is running right now.
        if (_journal is DbOperationJournal journal)
        {
            var self = journal.ProcessInstance + "/";
            var stale = await db.Operations.AsNoTracking()
                .Where(o => o.State == DbOperationJournal.Running && !o.Instance.StartsWith(self))
                .OrderBy(o => o.ClaimedAt)
                .ToListAsync(ct);
            foreach (var op in stale)
            {
                // The target's name is read first and is optional: once the row is marked, nothing may keep it from the
                // report — a later start no longer sees it.
                var target = await TargetOfAsync(db, op.AuditId, ct);
                // Conditional on the row as read: nothing else may have finished it meanwhile.
                var changed = await db.Operations
                    .Where(o => o.Id == op.Id && o.State == DbOperationJournal.Running && o.Instance == op.Instance)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.State, DbOperationJournal.Interrupted)
                        .SetProperty(o => o.FinishedAt, now), ct);
                if (changed == 0) continue;
                marked.Add(string.IsNullOrWhiteSpace(target)
                    ? $"{op.CapabilityId} (started {op.ClaimedAt:u})"
                    : $"{op.CapabilityId} on {target} (started {op.ClaimedAt:u})");
            }
        }

        // Task runs of an earlier process that never recorded their end.
        var open = await db.TaskRunHistory.AsNoTracking()
            .Where(r => r.CompletedAt == null && r.StartedAt < _processStart)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);
        foreach (var run in open)
        {
            // One transaction: the run is closed only together with its task's last result. Closed alone, a later start
            // would no longer see it and the task would keep its previous result.
            var closed = await InTransactionAsync(db, async () =>
            {
                // Conditional on the run still being open: one that ended meanwhile keeps its result.
                var changed = await db.TaskRunHistory
                    .Where(r => r.Id == run.Id && r.CompletedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.CompletedAt, now)
                        .SetProperty(r => r.Success, false)
                        .SetProperty(r => r.Output, (string?)null)
                        .SetProperty(r => r.Error, InterruptedResult), ct);
                if (changed == 0) return false;
                // Its task's last result too — only while that run is still the task's last one (a run is started
                // with the task's LastRun set to its own start in the same save), so a later run's result is kept.
                var startedAt = run.StartedAt;
                await db.ScheduledTasks
                    .Where(t => t.TaskId == run.TaskId && t.LastRun == startedAt)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.LastSuccess, false)
                        .SetProperty(t => t.LastResult, InterruptedResult), ct);
                return true;
            }, () => db.TaskRunHistory.AnyAsync(r => r.Id == run.Id && r.CompletedAt == now && r.Error == InterruptedResult, ct), ct);
            if (closed)
                marked.Add($"scheduled task {run.TaskName} (started {run.StartedAt:u})");
        }

        // AutoUpdate attempts of an earlier process that never recorded their end.
        var updates = await db.UpdateHistory.AsNoTracking()
            .Where(h => h.CompletedAt == null && h.StartedAt < _processStart)
            .OrderBy(h => h.StartedAt)
            .ToListAsync(ct);
        foreach (var update in updates)
        {
            // One transaction: the attempt is closed only together with switching its policy off. Closed alone, nothing
            // would keep the next check from starting the same update again.
            var (name, id, server) = (update.ContainerName, update.ContainerId, update.ServerId);
            var closed = await InTransactionAsync(db, async () =>
            {
                var changed = await db.UpdateHistory
                    .Where(h => h.Id == update.Id && h.CompletedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(h => h.CompletedAt, now)
                        .SetProperty(h => h.Success, false)
                        .SetProperty(h => h.Error, InterruptedResult), ct);
                if (changed == 0) return false;
                // Its policy is switched off: the update stays available and would otherwise be started again at the
                // next check — on a container that may be half replaced. Same scope as the update's own matching
                // (server, then id or name).
                await db.UpdatePolicies
                    .Where(p => p.AutoUpdate
                                && (p.ServerId == null || p.ServerId == "" || p.ServerId == server)
                                && (p.ContainerId == id || p.ContainerName == name))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.AutoUpdate, false)
                        .SetProperty(p => p.LastUpdateResult, InterruptedUpdateResult), ct);
                return true;
            }, () => db.UpdateHistory.AnyAsync(h => h.Id == update.Id && h.CompletedAt == now && h.Error == InterruptedResult, ct), ct);
            if (!closed) continue;
            marked.Add($"auto-update of {update.ContainerName} (started {update.StartedAt:u}; AutoUpdate switched off for it)");
        }

    }

    /// <summary>The audit row's target name, for the report; null when there is none or it cannot be read.</summary>
    private async Task<string?> TargetOfAsync(MetricsDbContext db, long? auditId, CancellationToken ct)
    {
        if (auditId is not { } id) return null;
        try
        {
            return await db.AuditLog.AsNoTracking().Where(a => a.Id == id).Select(a => a.TargetName).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The target of an interrupted operation could not be read; it is reported without it");
            return null;
        }
    }

    private async Task ReportAsync(IReadOnlyList<string> marked)
    {
        if (marked.Count == 0) return;
        _logger.LogWarning("Whiskers stopped while {Count} operation(s) ran; marked as interrupted: {Work}",
            marked.Count, string.Join("; ", marked));
        try
        {
            var named = string.Join("; ", marked.Take(MaxNamed))
                        + (marked.Count > MaxNamed ? $"; and {marked.Count - MaxNamed} more (see the log)" : "");
            await _notifications.SendAsync(new NotificationEvent
            {
                EventType = "operations_interrupted",
                ContainerName = "Whiskers",
                ImageInfo = $"Whiskers stopped while {marked.Count} operation(s) ran: {named}. Whether they took " +
                            "effect is unknown and none was run again — check the targets.",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Notification about interrupted operations failed");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the provider's execution strategy: PostgreSQL is registered
    /// with <c>EnableRetryOnFailure</c>, which refuses a transaction opened outside it. A retry after a commit whose
    /// outcome was lost asks <paramref name="committed"/> (this pass's own marks) instead of reporting nothing, so the
    /// work is neither done twice — every write is conditional — nor dropped from the report.
    /// </summary>
    private static Task<bool> InTransactionAsync(MetricsDbContext db, Func<Task<bool>> work, Func<Task<bool>> committed,
        CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var done = await work();
            if (!done)
            {
                await tx.RollbackAsync(ct);
                return await committed();
            }
            await tx.CommitAsync(ct);
            return true;
        });
}
