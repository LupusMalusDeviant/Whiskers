namespace Whiskers.Services.Maintenance;

/// <summary>In-memory implementation of <see cref="IMaintenanceStateService"/>. Registered as a Core singleton.</summary>
public sealed class MaintenanceStateService : IMaintenanceStateService
{
    private volatile bool _maintenance;

    public bool IsMaintenance => _maintenance;

    public string? Reason { get; private set; }

    public void EnterMaintenance(string reason)
    {
        Reason = reason;
        _maintenance = true;   // volatile write publishes Reason before the flag flips
    }

    public void ExitMaintenance()
    {
        _maintenance = false;
        Reason = null;
    }
}
