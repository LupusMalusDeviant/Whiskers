using Whiskers.Models;

namespace Whiskers.Services.Scheduler;

/// <summary>
/// The server a scheduled task acts on, one rule for the "Run now" gate, the scheduler's gates and the execution
/// (R1-06): a task saved without a server acts on the server with the id <c>local</c> — as it always has at
/// execution — never on "whichever server is the default", which may be another machine.
/// </summary>
public static class ScheduledTaskServer
{
    public static string Of(ScheduledTaskEntity task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.IsNullOrWhiteSpace(task.ServerId) ? "local" : task.ServerId;
    }
}
