namespace Whiskers.Services.AutoPatch;

/// <summary>Patches ONE container on its host and reports how it went.</summary>
public interface IAutoPatchRunner
{
    /// <param name="dryRun">Run every guard, recreate nothing.</param>
    /// <returns>The outcome plus the tail of the host-side log, for the report.</returns>
    [Whiskers.Services.Mutations.MutatingOperation("container.update")]
    Task<(AutoPatchOutcome Outcome, string LogTail)> PatchAsync(
        string serverId, [Whiskers.Services.Mutations.MutationTarget] string containerName, bool dryRun, CancellationToken ct = default);
}
