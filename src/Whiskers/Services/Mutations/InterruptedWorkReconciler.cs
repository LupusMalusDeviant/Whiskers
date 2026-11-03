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
            // Nothing is lost: the rows stay as they are and are classified the same way (outcome unknown) until the
            // next start tries again; an open AutoUpdate attempt keeps its container from being updated meanwhile.
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
                // Conditional on the row as read: nothing else may have finished it meanwhile.
                var changed = await db.Operations
                    .Where(o => o.Id == op.Id && o.State == DbOperationJournal.Running && o.Instance == op.Instance)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.State, DbOperationJournal.Interrupted)
                        .SetProperty(o => o.FinishedAt, now), ct);
                if (changed == 0) continue;
                var target = op.AuditId is { } auditId
                    ? await db.AuditLog.AsNoTracking().Where(a => a.Id == auditId).Select(a => a.TargetName).FirstOrDefaultAsync(ct)
                    : null;
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
            // Conditional on the run still being open: one that ended meanwhile keeps its result.
            var closed = await db.TaskRunHistory
                .Where(r => r.Id == run.Id && r.CompletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.Success, false)
                    .SetProperty(r => r.Output, (string?)null)
                    .SetProperty(r => r.Error, InterruptedResult), ct);
            if (closed == 0) continue;
            // Its task's last result too — only while that run is still the task's last one (a run is started with
            // the task's LastRun set to its own start in the same save), so a later run's result is never replaced.
            var startedAt = run.StartedAt;
            await db.ScheduledTasks
                .Where(t => t.TaskId == run.TaskId && t.LastRun == startedAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.LastSuccess, false)
                    .SetProperty(t => t.LastResult, InterruptedResult), ct);
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
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var closed = await db.UpdateHistory
                .Where(h => h.Id == update.Id && h.CompletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.CompletedAt, now)
                    .SetProperty(h => h.Success, false)
                    .SetProperty(h => h.Error, InterruptedResult), ct);
            if (closed == 0) continue;
            // Its policy is switched off: the update stays available and would otherwise be started again at the next
            // check — on a container that may be half replaced. Same scope as the update's own matching (server, then
            // id or name).
            var (name, id, server) = (update.ContainerName, update.ContainerId, update.ServerId);
            await db.UpdatePolicies
                .Where(p => p.AutoUpdate
                            && (p.ServerId == null || p.ServerId == "" || p.ServerId == server)
                            && (p.ContainerId == id || p.ContainerName == name))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.AutoUpdate, false)
                    .SetProperty(p => p.LastUpdateResult, InterruptedUpdateResult), ct);
            await tx.CommitAsync(ct);
            marked.Add($"auto-update of {update.ContainerName} (started {update.StartedAt:u}; AutoUpdate switched off for it)");
        }

        if (marked.Count == 0) return marked;
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
        return marked;
    }
}
