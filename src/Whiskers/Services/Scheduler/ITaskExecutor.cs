using Whiskers.Models;

namespace Whiskers.Services.Scheduler;

/// <summary>Executes a single scheduled task and returns its outcome.</summary>
public interface ITaskExecutor
{
    Task<(bool Success, string Output)> ExecuteAsync(ScheduledTaskEntity task);
}
