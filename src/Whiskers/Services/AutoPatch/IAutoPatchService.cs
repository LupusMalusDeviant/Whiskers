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

    /// <summary>Runs one cycle now. <paramref name="forceDryRun"/> keeps it a dry run whatever the settings
    /// say; there is deliberately no way to force the opposite from here.</summary>
    /// <returns>False when a cycle is already running.</returns>
    [MutatingOperation("autopatch.run")]
    Task<bool> RunCycleAsync(bool forceDryRun, CancellationToken ct = default);
}
