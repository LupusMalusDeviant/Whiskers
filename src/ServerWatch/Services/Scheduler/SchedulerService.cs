using Microsoft.EntityFrameworkCore;
using NCrontab;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Scheduler;

public class SchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskExecutor _executor;
    private readonly ILogger<SchedulerService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        TaskExecutor executor,
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

            // Calculate next run
            try
            {
                var cron = CrontabSchedule.Parse(task.CronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
                var nextRun = cron.GetNextOccurrence(now);

                // If this is the first run (NextRun was null), set NextRun and skip execution
                if (task.NextRun == null)
                {
                    task.NextRun = nextRun;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                // Execute the task
                _logger.LogInformation("Running scheduled task: {Name} (Type: {Type})", task.Name, task.TaskType);
                var startedAt = DateTime.UtcNow;

                var (success, output) = await _executor.ExecuteAsync(task);

                // Update task record
                task.LastRun = startedAt;
                task.LastResult = success ? output : $"FEHLER: {output}";
                task.LastSuccess = success;
                task.NextRun = cron.GetNextOccurrence(DateTime.UtcNow);

                // Save run history
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

                await db.SaveChangesAsync(ct);

                _logger.LogInformation("Task {Name} completed: success={Success}, next run: {NextRun}",
                    task.Name, success, task.NextRun);
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
