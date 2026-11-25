using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.AutoUpdate;

/// <summary>Background auto-update of container images; exposes policy + history for the UI.</summary>
public interface IAutoUpdateService
{
    Task<List<UpdatePolicyEntity>> GetPoliciesAsync();
    [MutatingOperation("autoupdate.policy_set")]
    Task SetPolicyAsync(UpdatePolicyEntity policy);
    Task<List<UpdateHistoryEntity>> GetHistoryAsync(string? containerId = null, int limit = 20);

    // C12 manual rollback: capture a container's pre-update snapshot (call this right before ANY update — the
    // auto-updater and the manual Dashboard update both do), list the captured snapshots, and roll a container
    // back to its previous image from its snapshot.
    Task CaptureSnapshotAsync(ContainerInfo container);
    Task<List<UpdateRollbackEntity>> GetRollbacksAsync();
    [MutatingOperation("container.rollback")]
    // Contract 0023: the token is observed after the snapshot is read, before the container is recreated; the recreate
    // itself runs to its end.
    Task<string> RollbackAsync(long rollbackId, CancellationToken ct = default);
    // R1-36: consumes the snapshot (and marks the update history) once the gateway verified the rollback — called
    // after the gated rollback succeeded, so a rollback that did not come up keeps its way back.
    Task CompleteRollbackAsync(long rollbackId);
}
