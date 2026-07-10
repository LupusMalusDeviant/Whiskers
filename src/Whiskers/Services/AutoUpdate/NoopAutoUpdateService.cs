using Whiskers.Models;

namespace Whiskers.Services.AutoUpdate;

/// <summary>The Core's default <see cref="IAutoUpdateService"/> for when the ImageUpdate module is off. Since
/// C12 the Dashboard page consumes it (the manual-rollback button reads <see cref="GetRollbacksAsync"/> and the
/// manual update path calls <see cref="CaptureSnapshotAsync"/>), so this no-op keeps that injection resolvable
/// with the module disabled — it captures nothing and lists no policies/history/rollbacks. The real
/// <see cref="AutoUpdateService"/> (a hosted background service) wins by last-registration when the module is
/// enabled. Soft-dependency-via-no-op-Core-contract pattern (RoadToSAP §2.1).</summary>
public sealed class NoopAutoUpdateService : IAutoUpdateService
{
    public Task<List<UpdatePolicyEntity>> GetPoliciesAsync() => Task.FromResult(new List<UpdatePolicyEntity>());
    public Task SetPolicyAsync(UpdatePolicyEntity policy) => Task.CompletedTask;
    public Task<List<UpdateHistoryEntity>> GetHistoryAsync(string? containerId = null, int limit = 20)
        => Task.FromResult(new List<UpdateHistoryEntity>());

    public Task CaptureSnapshotAsync(ContainerInfo container) => Task.CompletedTask;
    public Task<List<UpdateRollbackEntity>> GetRollbacksAsync() => Task.FromResult(new List<UpdateRollbackEntity>());
    // Unreachable in normal flow: GetRollbacksAsync returns none, so the UI never offers a rollback to invoke.
    public Task<string> RollbackAsync(long rollbackId)
        => throw new InvalidOperationException("Das Image-Updates-Modul ist deaktiviert — kein Rollback verfügbar.");
}
