using Whiskers.Services.Mutations;
namespace Whiskers.Services.AutoPatch;

public sealed record AutoPatchEntry(string ServerId, string ContainerName, AutoPatchResultKind Kind, string Detail);

public sealed record AutoPatchCycleReport(
    DateTime StartedAt,
    DateTime? CompletedAt,
    bool DryRun,
    IReadOnlyList<AutoPatchEntry> Entries,
    IReadOnlyList<AutoPatchSkip> Skipped,
    string? Error = null);

/// <summary>Fleet-wide security auto-patching: the nightly loop plus a manual dry run.</summary>
public interface IAutoPatchService
{
    /// <summary>The last cycle, finished or still running. Null before the first one.</summary>
    AutoPatchCycleReport? LastCycle { get; }

    bool IsRunning { get; }

    /// <summary>Runs one cycle now on behalf of <paramref name="onBehalfOf"/> (an automation owner id: an email or
    /// <c>key:&lt;id&gt;</c>); every container is decided on that authority, re-read per container.
    /// <paramref name="forceDryRun"/> keeps it a dry run whatever the settings say; there is deliberately no way to
    /// force the opposite from here.</summary>
    /// <returns>False when a cycle is already running, or a container's patch failed (unknown, rolled back).</returns>
    [MutatingOperation("autopatch.run")]
    Task<bool> RunCycleAsync(bool forceDryRun, string onBehalfOf, CancellationToken ct = default);
}
