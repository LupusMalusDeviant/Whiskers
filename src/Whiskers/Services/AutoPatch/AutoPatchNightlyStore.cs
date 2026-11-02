using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AutoPatch;

/// <summary>The last nightly cycle as recorded on disk (R1-39): its day, when it started and — once it ended — when.
/// A start without an end is a cycle a stop or a crash cut off.</summary>
public sealed class AutoPatchNightlyState
{
    public DateOnly? Day { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool DryRun { get; set; }
}

/// <summary>
/// Keeps the nightly cycle's state across restarts (R1-39, F-16): the day is written before the cycle starts, so a
/// restart in the window hour neither runs the night a second time nor — after a crash — forgets that it was cut off.
/// </summary>
public interface IAutoPatchNightlyStore
{
    Task<AutoPatchNightlyState> LoadAsync();
    Task SaveAsync(AutoPatchNightlyState state);
}

/// <inheritdoc cref="IAutoPatchNightlyStore"/>
public sealed class AutoPatchNightlyStore(DataPathOptions paths) : IAutoPatchNightlyStore
{
    private readonly JsonFileStore<AutoPatchNightlyState> _file = new(paths.AutoPatchNightlyJson);

    public Task<AutoPatchNightlyState> LoadAsync() => _file.LoadAsync();

    public Task SaveAsync(AutoPatchNightlyState state) => _file.SaveAsync(state);
}

/// <summary>Kept in memory only: for code built outside the host (tests).</summary>
public sealed class InMemoryAutoPatchNightlyStore : IAutoPatchNightlyStore
{
    private AutoPatchNightlyState _state = new();

    public Task<AutoPatchNightlyState> LoadAsync() => Task.FromResult(Copy(_state));

    public Task SaveAsync(AutoPatchNightlyState state)
    {
        _state = Copy(state);
        return Task.CompletedTask;
    }

    private static AutoPatchNightlyState Copy(AutoPatchNightlyState s) =>
        new() { Day = s.Day, StartedAt = s.StartedAt, CompletedAt = s.CompletedAt, DryRun = s.DryRun };
}
