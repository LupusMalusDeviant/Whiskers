using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Scheduler;

/// <summary>
/// Switches a scheduled task off because its owner lost their authority (PC-D6, R1-04) — but only the assignment the
/// run was started under. An admin who took the task over while the refused run was still in flight wins: the task
/// stays on and nothing is suspended.
/// </summary>
public interface IScheduledTaskSuspender
{
    /// <summary><c>true</c> when the task was switched off; <c>false</c> when it has another owner by now, was already
    /// off, or is gone.</summary>
    Task<bool> SuspendAsync(string taskId, string? expectedOwnerId, CancellationToken ct = default);
}

/// <summary>One conditional <c>UPDATE … WHERE TaskId AND CreatedBy AND Enabled</c>: atomic against a takeover.</summary>
public sealed class ScheduledTaskSuspender : IScheduledTaskSuspender
{
    private readonly IServiceScopeFactory _scopes;

    public ScheduledTaskSuspender(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    public async Task<bool> SuspendAsync(string taskId, string? expectedOwnerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var tasks = db.ScheduledTasks.Where(t => t.TaskId == taskId && t.Enabled);
        tasks = string.IsNullOrWhiteSpace(expectedOwnerId)
            ? tasks.Where(t => t.CreatedBy == null || t.CreatedBy == "")
            : tasks.Where(t => t.CreatedBy == expectedOwnerId);
        return await tasks.ExecuteUpdateAsync(s => s.SetProperty(t => t.Enabled, false), ct) > 0;
    }
}
