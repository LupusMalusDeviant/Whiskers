using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>An owner store held in memory, for services that read the AutoPatch / AutoUpdate owner.</summary>
internal sealed class MemoryAutomationOwners : IAutomationOwnerStore
{
    private readonly Dictionary<string, AutomationOwnerEntry> _entries = new(StringComparer.Ordinal);

    public HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);

    public AutomationOwnerEntry? Get(string feature) => Blocked.Contains(feature) ? null : _entries.GetValueOrDefault(feature);

    public bool IsBlocked(string feature) => Blocked.Contains(feature);

    public Task SetAsync(string feature, string ownerId, CancellationToken ct = default)
    {
        Blocked.Remove(feature);
        _entries[feature] = new AutomationOwnerEntry(ownerId, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Dictionary<string, string> Suspended { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, the suspension "cannot be saved": still blocked, but reported as not persisted.</summary>
    public bool FailToPersist { get; set; }

    public Task<SuspendResult> SuspendAsync(string feature, string? expectedOwnerId, string reason,
        CancellationToken ct = default)
    {
        if (Blocked.Contains(feature) || Get(feature)?.OwnerId != expectedOwnerId)
            return Task.FromResult(SuspendResult.Superseded);
        Blocked.Add(feature);
        Suspended[feature] = reason;
        return Task.FromResult(FailToPersist ? SuspendResult.NotSaved : SuspendResult.Suspended);
    }
}

/// <summary>For tests that drive a service past the point where it would hand work to the gateway.</summary>
internal sealed class UnusedAutomationRunner : IAutomationMutationRunner
{
    public Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default) =>
        throw new InvalidOperationException("This test does not expect an automation run.");

    public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task> action, CancellationToken ct = default) =>
        throw new InvalidOperationException("This test does not expect an automation run.");
}
