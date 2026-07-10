namespace Whiskers.Services.Maintenance;

/// <summary>
/// Process-wide maintenance flag. It is set for the brief window between a restore being committed and the
/// process restarting, so the maintenance middleware returns 503 instead of serving requests against state
/// that is about to be swapped out. In-memory only: a restart clears it, and the deferred restore file-swap
/// runs in Program.cs before the host is even built, so a freshly (re)started process is never in maintenance.
/// </summary>
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
}
