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
    /// <summary>Hands a task to <paramref name="ownerId"/> if its owner is still <paramref name="expectedOwnerId"/>
    /// (<c>null</c>: it has none) — one conditional update, so a takeover is never audited against an owner it did
    /// not replace. <paramref name="createdVia"/> records who did it (provenance only, defaults to the owner). Returns
    /// false when the task does not exist or its owner changed meanwhile.</summary>
    Task<bool> SetOwnerAsync(string taskId, string ownerId, string? expectedOwnerId, string? createdVia = null);
    Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50);
}
