using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Scheduler;

public class SchedulerService : Whiskers.Services.FleetBackgroundService, ISchedulerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITaskExecutor _executor;
    private readonly ILogger<SchedulerService> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    // Per-taskId in-flight guard so the same task never runs twice concurrently.
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly Whiskers.Services.Mutations.IShutdownDrain _drain;
    // A restore is being staged (F3): nothing starts until the restart — its audit events would land in the database
    // the restore replaces, after the pre-restore safety backup was taken.
    private readonly Whiskers.Services.Maintenance.IMaintenanceStateService? _maintenance;
    // Contract 0020: the first-run ledger of handed-over tasks (resume, takeover and delete keep it current).
    private readonly Whiskers.Services.Mutations.IHandoverLedger? _handovers;

    private static Whiskers.Services.Mutations.AutomationItemRef ItemOf(string taskId) =>
        new(Whiskers.Services.Mutations.AutomationKind.ScheduledTask, taskId);

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        ITaskExecutor executor,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        ILogger<SchedulerService> logger,
        Whiskers.Services.Mutations.IShutdownDrain? drain = null,
        Whiskers.Services.Maintenance.IMaintenanceStateService? maintenance = null,
        Whiskers.Services.Mutations.IHandoverLedger? handovers = null)
    {
        _handovers = handovers;
        _scopeFactory = scopeFactory;
        _executor = executor;
        _selfMetrics = selfMetrics;
        _logger = logger;
        _drain = drain ?? Whiskers.Services.Mutations.NoShutdownDrain.Instance;
        _maintenance = maintenance;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler service started. Check interval: {Interval}s", CheckInterval.TotalSeconds);

        // Initial delay to let app startup complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            var success = true;
            try
            {
                await CheckAndRunDueTasks(stoppingToken);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                success = false;
                _logger.LogError(ex, "Scheduler check failed");
            }

            // Reports like every other loop: a scheduler that stopped ticking would otherwise look exactly
            // like one with nothing due — and the nightly backups would silently stop with it.
            _selfMetrics.RecordCycle("scheduler", "*", DateTime.UtcNow - startedAt, success, CheckInterval);

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    internal async Task CheckAndRunDueTasks(CancellationToken ct)
    {
        // Restore maintenance: no due task starts and no occurrence is consumed; after the restart the restored schedule
        // decides. Nothing is written either — the database is about to be replaced.
        if (_maintenance?.IsMaintenance == true)
        {
            _logger.LogInformation("Scheduler paused: a restore is staged and Whiskers is about to restart");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var now = DateTime.UtcNow;
        var tasks = await db.ScheduledTasks
            .Where(t => t.Enabled && (t.NextRun == null || t.NextRun <= now))
            .ToListAsync(ct);

        foreach (var task in tasks)
        {
            if (ct.IsCancellationRequested) break;

            // A task whose cron won't parse is disabled (with a log) instead of erroring every 30s forever.
            if (!TryParseCron(task.CronExpression, out var cron))
            {
                _logger.LogWarning("Disabling scheduled task '{Name}': invalid cron expression '{Cron}'",
                    task.Name, task.CronExpression);
                task.Enabled = false;
                task.LastResult = $"FEHLER: ungültiger Cron-Ausdruck '{task.CronExpression}'";
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Calculate next run
            IDisposable? draining = null;
            var guarded = false;
            try
            {
                var nextRun = cron!.GetNextOccurrence(now);

                // If this is the first run (NextRun was null), set NextRun and skip execution
                if (task.NextRun == null)
                {
                    task.NextRun = nextRun;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                // R1-39: once Whiskers is stopping, nothing new starts — the occurrence stays due and runs after the
                // restart, exactly once (NextRun is advanced only when it really starts).
                draining = _drain.TryEnter($"scheduled task '{task.Name}'");
                if (draining is null)
                    break;
                // A restore that began since the check above: it waits for admitted runs, so this one is not started.
                draining = Both(draining, _maintenance, $"scheduled task '{task.Name}'");
                if (draining is null)
                    break;

                // Never the same task twice at once: an occurrence that falls due while the task still runs (a run
                // started by hand, or a long one) is skipped — NextRun moves on, LastRun stays the running one's.
                if (!_running.TryAdd(task.TaskId, 0))
                {
                    draining.Dispose();
                    draining = null;
                    task.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                guarded = true;

                // The occurrence is consumed and the run is on record in ONE save, before it starts (R1-39): a run
                // cut off by a stop or a crash stays open and the next start marks it interrupted, never as a success;
                // a failed save starts nothing and consumes nothing. Saved before starting, so a long-running task
                // doesn't re-trigger on the next 30s tick either.
                long runId;
                var startedAt = DateTime.UtcNow;
                var run = NewRun(task, startedAt);
                var (lastRun, dueAt) = (task.LastRun, task.NextRun);
                try
                {
                    task.LastRun = startedAt;
                    task.NextRun = cron.GetNextOccurrence(startedAt);
                    db.TaskRunHistory.Add(run);
                    await db.SaveChangesAsync(ct);
                    runId = run.Id;
                }
                catch
                {
                    // Nothing of it may be saved later with the error below: the occurrence stays due, no run is on
                    // record, and the error save does not consume it either.
                    db.Entry(run).State = EntityState.Detached;
                    (task.LastRun, task.NextRun) = (lastRun, dueAt);
                    throw;
                }

                _logger.LogInformation("Running scheduled task: {Name} (Type: {Type})", task.Name, task.TaskType);
                var taskId = task.TaskId;
                var entry = draining;
                draining = null;   // the run owns it now
                guarded = false;   // and the in-flight guard
                _ = Task.Run(() => RunTaskAsync(taskId, runId, entry), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process task {Name}", task.Name);
                if (!guarded)
                {
                    // Not this tick's run (the task runs, or nothing was started): no result of its own to record — a
                    // running task's result is its run's. The unsaved change is dropped; the next tick tries again.
                    db.Entry(task).State = EntityState.Detached;
                    draining?.Dispose();
                    continue;
                }
                try
                {
                    // Only the error: nothing ran, so LastRun stays the last real run's (R1-39). Still under the in-flight
                    // guard, so a "Run now" cannot start in between and have its result overwritten by this one.
                    task.LastResult = $"FEHLER: {ex.Message}";
                    task.LastSuccess = false;
                    await db.SaveChangesAsync(ct);
                }
                finally
                {
                    if (guarded) _running.TryRemove(task.TaskId, out _);
                    draining?.Dispose();
                }
            }
        }
    }

    /// <summary>Runs one due task in the background (its own DB scope), recording the result + history and
    /// clearing the in-flight guard when done. NextRun/LastRun were already persisted by the caller.</summary>
    private async Task RunTaskAsync(string taskId, long runId, IDisposable draining)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null)
            {
                await CloseRunAsync(db, runId, false, "The task was deleted before it ran.");
                return;
            }

            var (success, output) = await _executor.ExecuteAsync(task);

            await RecordResultAsync(db, task, runId, success, output);

            _logger.LogInformation("Task {Name} completed: success={Success}", task.Name, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task {TaskId} failed", taskId);
            await TryCloseFailedRunAsync(runId, ex);
        }
        finally
        {
            _running.TryRemove(taskId, out _);
            draining.Dispose();
        }
    }

    /// <summary>The run's drain entry together with its maintenance entry (a staged restore waits for it), or null — with
    /// the drain entry released — when a restore is staged.</summary>
    private static IDisposable? Both(IDisposable draining, Whiskers.Services.Maintenance.IMaintenanceStateService? maintenance,
        string what)
    {
        if (maintenance is null) return draining;
        var admitted = maintenance.TryEnter(what);
        if (admitted is null)
        {
            draining.Dispose();
            return null;
        }
        return new Handles(draining, admitted);
    }

    private sealed class Handles(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            try { second.Dispose(); }
            finally { first.Dispose(); }
        }
    }

    /// <summary>A run as started: no end yet (R1-39).</summary>
    private static TaskRunHistoryEntity NewRun(ScheduledTaskEntity task, DateTime startedAt) => new()
    {
        TaskId = task.TaskId,
        TaskName = task.Name,
        TaskType = task.TaskType,
        StartedAt = startedAt,
        CompletedAt = null,
        Success = false,
        ProcessInstance = Whiskers.Services.Mutations.ProcessIdentity.Current,
    };

    /// <summary>A run whose record could not be finished normally (the database failed while it was read or closed) is
    /// closed as failed, best effort and in a scope of its own — it was not cut off by a stop, so the next start must not
    /// report it as interrupted. A second failure is only logged.</summary>
    private async Task TryCloseFailedRunAsync(long runId, Exception failure)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var run = await db.TaskRunHistory.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is null || run.CompletedAt is not null) return;
            await CloseRunAsync(db, runId, false, $"The run's result could not be recorded: {failure.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The failed run {RunId} could not be closed; the next start reports it as interrupted", runId);
        }
    }

    /// <summary>Records a run's end together with its task's last result. A task deleted while it ran has no row to
    /// update any more: its run keeps the result it really had (not "failed"), and nothing throws.</summary>
    private async Task RecordResultAsync(MetricsDbContext db, ScheduledTaskEntity task, long runId, bool success, string output)
    {
        task.LastResult = success ? output : $"FEHLER: {output}";
        task.LastSuccess = success;
        try
        {
            await CloseRunAsync(db, runId, success, output);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(e => e.Entity is ScheduledTaskEntity))
        {
            _logger.LogInformation("Task {Name} was deleted while it ran; its run is recorded without a task result", task.Name);
            foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Records the end of a run opened by <see cref="NewRun"/>, together with the tracked task's
    /// result. Not cancellable: a run that ended is recorded as such, also while Whiskers is stopping.</summary>
    private static async Task CloseRunAsync(MetricsDbContext db, long runId, bool success, string output)
    {
        var run = await db.TaskRunHistory.FirstOrDefaultAsync(r => r.Id == runId);
        if (run is not null)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Success = success;
            run.Output = success ? output : null;
            run.Error = success ? null : output;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>How far ahead a schedule must fire at least once to count as valid: covers 29 February, which can be
    /// eight years away (2096 → 2104).</summary>
    private const int MaxYearsToNextOccurrence = 9;

    /// <summary>Parses a cron expression, returning false instead of throwing on an invalid one — also for one that
    /// parses but never fires (30 February, 31 April), whose next run would otherwise be the year 9999.</summary>
    public static bool TryParseCron(string expression, out CrontabSchedule? schedule)
    {
        try
        {
            var parsed = CrontabSchedule.Parse(expression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
            var from = DateTime.UtcNow;
            var horizon = from.AddYears(MaxYearsToNextOccurrence);
            schedule = parsed.GetNextOccurrence(from, horizon) < horizon ? parsed : null;
            return schedule is not null;
        }
        catch
        {
            schedule = null;
            return false;
        }
    }

    /// <summary>A task time as the cron reads it: in UTC and marked so. Shown in the server's local time beside a UTC
    /// cron, "0 3 * * *" read as "04:00" on a CET host — the run looked shifted by the UTC offset.</summary>
    public static string FormatUtc(DateTime? time, string format = "yyyy-MM-dd HH:mm")
        => time is { } t
            ? (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime())
                .ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " UTC"
            : "";

    private static CrontabSchedule ParseOrThrow(string expression)
        => TryParseCron(expression, out var cron)
            ? cron!
            : throw new ArgumentException(
                $"Invalid cron expression '{expression}': it does not parse or never fires (e.g. 30 February).",
                nameof(expression));

    // === Public API for manual task management ===

    public async Task<List<ScheduledTaskEntity>> GetTasksAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.ScheduledTasks.OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task)
    {
        var cron = ParseOrThrow(task.CronExpression);
        task.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync();

        _logger.LogInformation("Scheduled task created: {Name} ({Cron}), next run: {NextRun}", task.Name, task.CronExpression, task.NextRun);
        return task;
    }

    public async Task UpdateTaskAsync(ScheduledTaskEntity task)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == task.TaskId);
        if (existing == null) return;
        var cron = ParseOrThrow(task.CronExpression);   // before anything changes
        var switchedOn = !existing.Enabled && task.Enabled;

        existing.Name = task.Name;
        existing.CronExpression = task.CronExpression;
        existing.TaskType = task.TaskType;
        existing.TargetId = task.TargetId;
        existing.TargetName = task.TargetName;
        existing.ServerId = task.ServerId;
        existing.Enabled = task.Enabled;
        existing.Config = task.Config;

        existing.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);

        await db.SaveChangesAsync();
        // Contract 0020 (D7): switching a handed-over task on is its resume; the owner stays, the resumer is recorded —
        // only once the switch is saved, so a failed save records no resume.
        if (switchedOn)
            Whiskers.Services.Mutations.HandoverNotes.RecordResumeInScope(_handovers, ItemOf(existing.TaskId), _logger);
    }

    public async Task<bool> SetOwnerAsync(string taskId, string ownerId, string? expectedOwnerId,
        string? createdVia = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var via = createdVia ?? ownerId;
        var tasks = db.ScheduledTasks.Where(t => t.TaskId == taskId);
        tasks = string.IsNullOrWhiteSpace(expectedOwnerId)
            ? tasks.Where(t => t.CreatedBy == null || t.CreatedBy == "")
            : tasks.Where(t => t.CreatedBy == expectedOwnerId);
        var taken = await tasks.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CreatedBy, ownerId)
            .SetProperty(t => t.CreatedVia, via)) == 1;
        // A takeover as today ends a handover's first-run note: the task is the taker's now.
        if (taken) Whiskers.Services.Mutations.HandoverNotes.ForgetQuietly(_handovers, ItemOf(taskId), _logger);
        return taken;
    }

    public async Task DeleteTaskAsync(string taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task != null)
        {
            db.ScheduledTasks.Remove(task);
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.HandoverNotes.ForgetQuietly(_handovers, ItemOf(taskId), _logger);
        }
    }

    public Task<Whiskers.Services.Mutations.HandoverWrite> HandOverAsync(string taskId, string fromOwnerId,
        string toOwnerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        return SwitchOffAsync(taskId, fromOwnerId, toOwnerId, ct);
    }

    public Task<Whiskers.Services.Mutations.HandoverWrite> HoldAsync(string taskId, string ownerId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return SwitchOffAsync(taskId, ownerId, ownerId, ct);
    }

    /// <summary>Contract 0020: one conditional update — owner <paramref name="from"/> → <paramref name="to"/> (the same
    /// for a hold), switched off, provenance unchanged (D5). Conditional on the state read, so "was on" is what it
    /// replaced; a toggle in between is read again.</summary>
    private async Task<Whiskers.Services.Mutations.HandoverWrite> SwitchOffAsync(string taskId, string from, string to,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId, ct);
            if (before is null)
                return new(Whiskers.Services.Mutations.HandoverItemStatus.SkippedGone, taskId, false);
            if (before.CreatedBy != from)
                return new(Whiskers.Services.Mutations.HandoverItemStatus.SkippedOwnerChanged, before.Name, before.Enabled);
            var wasOn = before.Enabled;
            var changed = await db.ScheduledTasks
                .Where(t => t.TaskId == taskId && t.CreatedBy == from && t.Enabled == wasOn)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedBy, to).SetProperty(t => t.Enabled, false), ct);
            if (changed == 1) return new(Whiskers.Services.Mutations.HandoverItemStatus.HandedOver, before.Name, wasOn);
        }
        throw new InvalidOperationException("The task kept changing while it was handed over; nothing was written.");
    }

    /// <summary>Stable result of <see cref="RunNowAsync"/> while Whiskers stops; the UI shows it translated.</summary>
    public const string ShuttingDownResult = "Whiskers is shutting down; the task was not started.";

    /// <summary>Stable result of <see cref="RunNowAsync"/> while the task runs; the UI shows it translated.</summary>
    public const string AlreadyRunningResult = "The task is already running.";

    /// <summary>Stable result of <see cref="RunNowAsync"/> while a restore is staged; the UI shows it translated.</summary>
    public const string MaintenanceResult = "A restore is in progress and Whiskers is about to restart; the task was not started.";

    /// <summary>Contract 0020: why "Run now" refuses <paramref name="task"/> (<c>AutomationHeld: …</c>), or <c>null</c>. The
    /// signal: the task is off <b>and</b> the ledger holds an entry for it that nobody resumed. A task switched on again
    /// is resumed (even if its resume could not be recorded); a task that is off for another reason has no entry. A
    /// ledger that cannot be read refuses an off task (fail closed).</summary>
    private string? HeldResult(ScheduledTaskEntity task)
    {
        if (task.Enabled || _handovers is null) return null;
        var item = ItemOf(task.TaskId);
        try
        {
            if (!_handovers.AwaitsResume(item)) return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The handover ledger could not be read for a run of task {TaskId}; the run is refused", task.TaskId);
            return $"{Whiskers.Services.Mutations.MutationReasonCodes.AutomationHeld}: whether '{task.Name}' was handed over " +
                   "cannot be read (handover ledger unreadable); nothing ran.";
        }
        return $"{Whiskers.Services.Mutations.MutationReasonCodes.AutomationHeld}: '{task.Name}' was handed over or held and " +
               "stays off until it is switched on; nothing ran.";
    }

    public async Task<(bool Success, string Output)> RunNowAsync(string taskId)
    {
        if (_maintenance?.IsMaintenance == true) return (false, MaintenanceResult);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return (false, "Task not found");
        // Contract 0020: a handed-over or held task stays off until someone switches it on; "Run now" is no way around
        // that (it would run on the new owner's authority before anyone resumed it). Refused, before anything is recorded.
        if (HeldResult(task) is { } held) return (false, Whiskers.Services.Mutations.MutationScope.Fail(held));

        // R1-39: a run by hand is admitted like a scheduled one — not while Whiskers stops, not while the task runs —
        // and the drain waits for it until its result is recorded.
        using var draining = _drain.TryEnter($"scheduled task '{task.Name}' (run now)");
        if (draining is null) return (false, ShuttingDownResult);
        using var admitted = _maintenance?.TryEnter($"scheduled task '{task.Name}' (run now)");
        if (_maintenance is not null && admitted is null) return (false, MaintenanceResult);
        if (!_running.TryAdd(task.TaskId, 0)) return (false, AlreadyRunningResult);
        long? runId = null;
        try
        {
            // On record before it runs, together with its start: a run a stop cuts off is reported as interrupted at
            // the next start.
            var startedAt = DateTime.UtcNow;
            var run = NewRun(task, startedAt);
            task.LastRun = startedAt;
            db.TaskRunHistory.Add(run);
            await db.SaveChangesAsync();

            runId = run.Id;

            var (success, output) = await _executor.ExecuteAsync(task);

            await RecordResultAsync(db, task, run.Id, success, output);
            return (success, output);
        }
        catch (Exception ex) when (runId is { } opened)
        {
            await TryCloseFailedRunAsync(opened, ex);
            throw;
        }
        finally
        {
            _running.TryRemove(task.TaskId, out _);
        }
    }

    public async Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<TaskRunHistoryEntity> query = db.TaskRunHistory;
        if (!string.IsNullOrEmpty(taskId))
            query = query.Where(h => h.TaskId == taskId);

        return await query.OrderByDescending(h => h.StartedAt).Take(limit).ToListAsync();
    }
}
