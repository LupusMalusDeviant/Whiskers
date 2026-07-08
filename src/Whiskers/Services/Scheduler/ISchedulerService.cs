using Whiskers.Models;

namespace Whiskers.Services.Scheduler;

/// <summary>Manages cron-scheduled tasks and their run history.</summary>
public interface ISchedulerService
{
    Task<List<ScheduledTaskEntity>> GetTasksAsync();
    Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task);
    Task UpdateTaskAsync(ScheduledTaskEntity task);
    Task DeleteTaskAsync(string taskId);
    Task<(bool Success, string Output)> RunNowAsync(string taskId);
    Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50);
}
