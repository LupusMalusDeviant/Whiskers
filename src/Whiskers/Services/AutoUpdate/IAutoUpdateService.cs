using Whiskers.Models;

namespace Whiskers.Services.AutoUpdate;

/// <summary>Background auto-update of container images; exposes policy + history for the UI.</summary>
public interface IAutoUpdateService
{
    Task<List<UpdatePolicyEntity>> GetPoliciesAsync();
    Task SetPolicyAsync(UpdatePolicyEntity policy);
    Task<List<UpdateHistoryEntity>> GetHistoryAsync(string? containerId = null, int limit = 20);

    // C12 manual rollback: capture a container's pre-update snapshot (call this right before ANY update — the
    // auto-updater and the manual Dashboard update both do), list the captured snapshots, and roll a container
    // back to its previous image from its snapshot.
    Task CaptureSnapshotAsync(ContainerInfo container);
    Task<List<UpdateRollbackEntity>> GetRollbacksAsync();
    Task<string> RollbackAsync(long rollbackId);
}
