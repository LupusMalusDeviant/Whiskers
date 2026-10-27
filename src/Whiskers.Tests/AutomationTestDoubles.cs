using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>An owner store held in memory, for services that read the AutoPatch / AutoUpdate owner.</summary>
internal sealed class MemoryAutomationOwners : IAutomationOwnerStore
{
    private readonly Dictionary<string, AutomationOwnerEntry> _entries = new(StringComparer.Ordinal);

    public AutomationOwnerEntry? Get(string feature) => _entries.GetValueOrDefault(feature);

    public Task SetAsync(string feature, string ownerId, CancellationToken ct = default)
    {
        _entries[feature] = new AutomationOwnerEntry(ownerId, DateTime.UtcNow);
        return Task.CompletedTask;
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
