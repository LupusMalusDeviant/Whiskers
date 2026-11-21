namespace Whiskers.Services.Maintenance;

/// <summary>
/// Process-wide maintenance flag. It is set for the brief window between a restore being committed and the
/// process restarting, so the maintenance middleware returns 503 instead of serving requests against state
/// that is about to be swapped out. In-memory only: a restart clears it, and the deferred restore file-swap
/// runs in Program.cs before the host is even built, so a freshly (re)started process is never in maintenance.
/// </summary>
/// <remarks>It also counts the work that must not overlap a restore — every Mutation Gateway operation and every
/// scheduled task run (<see cref="TryEnter"/>) — so the restore can wait for it (<see cref="WaitForIdleAsync"/>) before
/// it takes the pre-restore safety backup. Entering maintenance and admitting work are decided under one lock: once
/// the flag is set nothing new is admitted, so what the wait sees running is all that can still write.</remarks>
public interface IMaintenanceStateService
{
    /// <summary>True while the application is in maintenance and should not serve normal requests.</summary>
    bool IsMaintenance { get; }

    /// <summary>Human-readable reason shown on the 503 maintenance page, or null when not in maintenance.</summary>
    string? Reason { get; }

    /// <summary>Enters maintenance mode. Idempotent.</summary>
    void EnterMaintenance(string reason);

    /// <summary>Leaves maintenance mode. Only for the abort path of a restore that failed BEFORE it committed
    /// (so the app resumes normal service); a committed restore instead ends in a process restart, which clears
    /// the flag on its own.</summary>
    void ExitMaintenance();

    /// <summary>Registers one piece of work that writes state (a gateway operation, a scheduled run). Returns <c>null</c>
    /// while in maintenance — the caller then does not start it. Dispose the handle when the work has ended.</summary>
    IDisposable? TryEnter(string what);

    /// <summary>What is registered as running right now, oldest first.</summary>
    IReadOnlyList<string> InFlight { get; }

    /// <summary>Waits until no registered work runs, at most <paramref name="budget"/>. True when idle.</summary>
    Task<bool> WaitForIdleAsync(TimeSpan budget, CancellationToken ct = default);
}
