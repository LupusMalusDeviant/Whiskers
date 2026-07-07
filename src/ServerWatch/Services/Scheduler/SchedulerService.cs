using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Scheduler;

public class SchedulerService : BackgroundService, ISchedulerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITaskExecutor _executor;
    private readonly ILogger<SchedulerService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    // Per-taskId in-flight guard so the same task never runs twice concurrently.
    private readonly ConcurrentDictionary<string, byte> _running = new();

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        ITaskExecutor executor,
        ILogger<SchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler service started. Check interval: {Interval}s", CheckInterval.TotalSeconds);

        // Initial delay to let app startup complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunDueTasks(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Scheduler check failed");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAndRunDueTasks(CancellationToken ct)
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

                // Persist NextRun/LastRun BEFORE starting so a long-running task doesn't re-trigger on the
                // next 30s tick (it's no longer "due", and the in-flight guard backs that up).
                task.LastRun = DateTime.UtcNow;
                task.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);
                await db.SaveChangesAsync(ct);

                // Fire without blocking the loop, but never run the same task twice at once. TryAdd AFTER the
                // save so a failed persist can't leak the in-flight guard.
                if (!_running.TryAdd(task.TaskId, 0))
                    continue;

                _logger.LogInformation("Running scheduled task: {Name} (Type: {Type})", task.Name, task.TaskType);
                var taskId = task.TaskId;
                _ = Task.Run(() => RunTaskAsync(taskId), CancellationToken.None);
            }
            catch (Exception ex)
            {
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
    private async Task RunTaskAsync(string taskId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
            if (task == null) return;

            var startedAt = task.LastRun ?? DateTime.UtcNow;
            var (success, output) = await _executor.ExecuteAsync(task);

            task.LastResult = success ? output : $"FEHLER: {output}";
            task.LastSuccess = success;
            db.TaskRunHistory.Add(new TaskRunHistoryEntity
            {
                TaskId = task.TaskId,
                TaskName = task.Name,
                TaskType = task.TaskType,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = success,
                Output = success ? output : null,
                Error = success ? null : output
            });
            await db.SaveChangesAsync();

            _logger.LogInformation("Task {Name} completed: success={Success}", task.Name, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task {TaskId} failed", taskId);
        }
        finally
        {
            _running.TryRemove(taskId, out _);
        }
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

        var (success, output) = await _executor.ExecuteAsync(task);

        task.LastRun = DateTime.UtcNow;
        task.LastResult = success ? output : $"FEHLER: {output}";
        task.LastSuccess = success;

        db.TaskRunHistory.Add(new TaskRunHistoryEntity
        {
            TaskId = task.TaskId,
            TaskName = task.Name,
            TaskType = task.TaskType,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Success = success,
            Output = success ? output : null,
            Error = success ? null : output
        });

        await db.SaveChangesAsync();
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
