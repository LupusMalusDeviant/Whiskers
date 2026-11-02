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

                // Persist NextRun/LastRun BEFORE starting so a long-running task doesn't re-trigger on the
                // next 30s tick (it's no longer "due", and the in-flight guard backs that up).
                task.LastRun = DateTime.UtcNow;
                task.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);
                await db.SaveChangesAsync(ct);

                // Fire without blocking the loop, but never run the same task twice at once. TryAdd AFTER the
                // save so a failed persist can't leak the in-flight guard.
                if (!_running.TryAdd(task.TaskId, 0))
                {
                    draining.Dispose();
                    continue;
                }

                // The run is on record before it starts (R1-39): a run cut off by a stop or a crash stays open and
                // the next start marks it interrupted, never as a success.
                long runId;
                try
                {
                    runId = await OpenRunAsync(db, task, task.LastRun.Value, ct);
                }
                catch
                {
                    _running.TryRemove(task.TaskId, out _);
                    throw;
                }

                _logger.LogInformation("Running scheduled task: {Name} (Type: {Type})", task.Name, task.TaskType);
                var taskId = task.TaskId;
                var entry = draining;
                draining = null;   // the run owns it now
                _ = Task.Run(() => RunTaskAsync(taskId, runId, entry), CancellationToken.None);
            }
            catch (Exception ex)
            {
                draining?.Dispose();
                _logger.LogError(ex, "Failed to process task {Name}", task.Name);
                task.LastResult = $"FEHLER: {ex.Message}";
                task.LastSuccess = false;
                task.LastRun = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
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

    /// <summary>Records a run as started (no end yet) and returns its id (R1-39).</summary>
    private static async Task<long> OpenRunAsync(MetricsDbContext db, ScheduledTaskEntity task, DateTime startedAt,
        CancellationToken ct = default)
    {
        var run = new TaskRunHistoryEntity
        {
            TaskId = task.TaskId,
            TaskName = task.Name,
            TaskType = task.TaskType,
            StartedAt = startedAt,
            CompletedAt = null,
            Success = false,
        };
        db.TaskRunHistory.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

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

    public async Task<(bool Success, string Output)> RunNowAsync(string taskId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return (false, "Task not found");

        // On record before it runs (R1-39): a run a stop cuts off is reported as interrupted at the next start.
        var runId = await OpenRunAsync(db, task, DateTime.UtcNow);
        var (success, output) = await _executor.ExecuteAsync(task);

        task.LastRun = DateTime.UtcNow;
        task.LastResult = success ? output : $"FEHLER: {output}";
        task.LastSuccess = success;
        await CloseRunAsync(db, runId, success, output);
        return (success, output);
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
