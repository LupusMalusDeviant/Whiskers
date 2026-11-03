using System.Text.Json;
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
    /// <summary>Why a night that ended ended early (a stop, a suspension, an unverified rollback); safe to show.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Keeps the nightly cycle's state across restarts (R1-39, F-16): the day is written before the cycle starts, so a
/// restart in the window hour neither runs the night a second time nor — after a crash — forgets that it was cut off.
/// </summary>
public interface IAutoPatchNightlyStore
{
    /// <summary>The recorded state; an empty one when none was ever written. Throws when the file exists but cannot be
    /// read — the caller must not take that for "no night ran".</summary>
    Task<AutoPatchNightlyState> LoadAsync();
    Task SaveAsync(AutoPatchNightlyState state);
}

/// <inheritdoc cref="IAutoPatchNightlyStore"/>
/// <remarks>Deliberately not a <see cref="JsonFileStore{T}"/>: that one falls back to the previous generation when the
/// file is damaged, and the previous generation of this file is the night before — loading it would make tonight due
/// again. Here a damaged file stays an error (fail closed: no night runs until it is fixed or removed), and a write is
/// published atomically without keeping a previous generation.</remarks>
public sealed class AutoPatchNightlyStore(DataPathOptions paths) : IAutoPatchNightlyStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path = paths.AutoPatchNightlyJson;

    public async Task<AutoPatchNightlyState> LoadAsync()
    {
        if (!File.Exists(_path)) return new AutoPatchNightlyState();
        var text = await File.ReadAllTextAsync(_path);
        return JsonSerializer.Deserialize<AutoPatchNightlyState>(text, Json)
               ?? throw new JsonException("The nightly AutoPatch state is empty.");
    }

    public async Task SaveAsync(AutoPatchNightlyState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // The error is free text (a gateway's failure reason can carry an exception message): redacted like every other
        // persistent sink (INV-51) before it is written.
        var saved = new AutoPatchNightlyState
        {
            Day = state.Day, StartedAt = state.StartedAt, CompletedAt = state.CompletedAt, DryRun = state.DryRun,
            Error = Whiskers.Utils.SinkRedaction.Text(state.Error),
        };
        await DurableFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(saved, Json), keepPrevious: false);
    }
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
        new() { Day = s.Day, StartedAt = s.StartedAt, CompletedAt = s.CompletedAt, DryRun = s.DryRun, Error = s.Error };
}
