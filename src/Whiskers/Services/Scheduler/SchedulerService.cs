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

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        ITaskExecutor executor,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        ILogger<SchedulerService> logger,
        Whiskers.Services.Mutations.IShutdownDrain? drain = null)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _selfMetrics = selfMetrics;
        _logger = logger;
        _drain = drain ?? Whiskers.Services.Mutations.NoShutdownDrain.Instance;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            task.LastResult = success ? output : $"FEHLER: {output}";
            task.LastSuccess = success;
            await CloseRunAsync(db, runId, success, output);

            _logger.LogInformation("Task {Name} completed: success={Success}", task.Name, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task {TaskId} failed", taskId);
        }
        finally
        {
            _running.TryRemove(taskId, out _);
            draining.Dispose();
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
    };

    /// <summary>Records the end of a run opened by <see cref="OpenRunAsync"/>, together with the tracked task's
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

    /// <summary>Parses a cron expression, returning false instead of throwing on an invalid one.</summary>
    public static bool TryParseCron(string expression, out CrontabSchedule? schedule)
    {
        try
        {
            schedule = CrontabSchedule.Parse(expression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
            return true;
        }
        catch
        {
            schedule = null;
            return false;
        }
    }

    // === Public API for manual task management ===

    public async Task<List<ScheduledTaskEntity>> GetTasksAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.ScheduledTasks.OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task)
    {
        // Validate cron
        CrontabSchedule.Parse(task.CronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });

        var cron = CrontabSchedule.Parse(task.CronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
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

        existing.Name = task.Name;
        existing.CronExpression = task.CronExpression;
        existing.TaskType = task.TaskType;
        existing.TargetId = task.TargetId;
        existing.TargetName = task.TargetName;
        existing.ServerId = task.ServerId;
        existing.Enabled = task.Enabled;
        existing.Config = task.Config;

        var cron = CrontabSchedule.Parse(task.CronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
        existing.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);

        await db.SaveChangesAsync();
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
        return await tasks.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CreatedBy, ownerId)
            .SetProperty(t => t.CreatedVia, via)) == 1;
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
        }
    }

    /// <summary>Stable result of <see cref="RunNowAsync"/> while Whiskers stops; the UI shows it translated.</summary>
    public const string ShuttingDownResult = "Whiskers is shutting down; the task was not started.";

    /// <summary>Stable result of <see cref="RunNowAsync"/> while the task runs; the UI shows it translated.</summary>
    public const string AlreadyRunningResult = "The task is already running.";

    public async Task<(bool Success, string Output)> RunNowAsync(string taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return (false, "Task not found");

        // R1-39: a run by hand is admitted like a scheduled one — not while Whiskers stops, not while the task runs —
        // and the drain waits for it until its result is recorded.
        using var draining = _drain.TryEnter($"scheduled task '{task.Name}' (run now)");
        if (draining is null) return (false, ShuttingDownResult);
        if (!_running.TryAdd(task.TaskId, 0)) return (false, AlreadyRunningResult);
        try
        {
            // On record before it runs, together with its start: a run a stop cuts off is reported as interrupted at
            // the next start.
            var startedAt = DateTime.UtcNow;
            var run = NewRun(task, startedAt);
            task.LastRun = startedAt;
            db.TaskRunHistory.Add(run);
            await db.SaveChangesAsync();

            var (success, output) = await _executor.ExecuteAsync(task);

            task.LastResult = success ? output : $"FEHLER: {output}";
            task.LastSuccess = success;
            await CloseRunAsync(db, run.Id, success, output);
            return (success, output);
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
