namespace Whiskers.Services.Mutations;

/// <summary>
/// The start-up pass over work an earlier process left unfinished (R1-39, <see cref="InterruptedWorkReconciler"/>).
/// <see cref="Completed"/> ends when the pass has run — also when it failed — so a loop that must not act on stale state
/// (AutoUpdate: a policy the pass switches off) waits for it before its first cycle.
/// </summary>
public interface IStartupReconciliation
{
    Task Completed { get; }
}

/// <summary>Nothing to wait for: for code built outside the host (tests, tools).</summary>
public sealed class NoStartupReconciliation : IStartupReconciliation
{
    public static readonly NoStartupReconciliation Instance = new();
    public Task Completed => Task.CompletedTask;
}
