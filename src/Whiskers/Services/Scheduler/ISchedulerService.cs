using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Scheduler;

/// <summary>Manages cron-scheduled tasks and their run history.</summary>
public interface ISchedulerService
{
    Task<List<ScheduledTaskEntity>> GetTasksAsync();
    [MutatingOperation("scheduler.create")]
    Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task);
    [MutatingOperation("scheduler.update")]
    Task UpdateTaskAsync(ScheduledTaskEntity task);
    [MutatingOperation("scheduler.delete")]
    Task DeleteTaskAsync(string taskId);
    [MutatingOperation("scheduler.run")]
    Task<(bool Success, string Output)> RunNowAsync(string taskId);
    /// <summary>Makes <paramref name="ownerId"/> the task's owner: every later run uses that owner's authority
    /// (PC-D6). Returns false when the task does not exist.</summary>
    [MutatingOperation("scheduler.update")]
    Task<bool> SetOwnerAsync(string taskId, string ownerId);
    Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50);
}
