using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>R1-56 (review MAJOR-4): the threshold breaches open when Whiskers stopped, as the file keeps them.</summary>
public sealed class AlertBreachFile
{
    /// <summary>Container CPU/memory and host disk (<c>high_cpu</c>, <c>high_memory</c>, <c>high_disk</c>).</summary>
    public List<BreachState> Thresholds { get; set; } = [];

    /// <summary>Host CPU, memory and unexplained load (<see cref="HostLoadEvaluator"/>).</summary>
    public List<BreachState> HostLoad { get; set; } = [];

    /// <summary>Review round 2: the server of each container with a kept breach, so it is known before the first listing.</summary>
    public List<ContainerOwner>? Containers { get; set; } = [];

    /// <summary>Follow-up (2026-11-03, review R1-56 MINOR-1): the reported <c>metrics_gap</c>s still open, so each is closed
    /// once after a restart and not raised a second time. Optional: a file written before has none (read as empty).</summary>
    public List<GapState>? Gaps { get; set; } = [];
}

/// <summary>R1-56 (review round 2): which server a container with a kept breach belongs to.</summary>
public sealed record ContainerOwner(string Id, string ServerId, string? ServerName, string? Name);

/// <summary>
/// R1-56 (review MAJOR-4): keeps the open threshold breaches across a restart, so a breach that was raised before Whiskers
/// restarted (its own auto-update, a redeploy) is closed with its recovery afterwards — and is not raised a second time as
/// if it were new. Local to the install (<c>alert-breaches.json</c>, not part of a self-backup: a restored breach state
/// would name alerts the channels of this install never got).
/// </summary>
public interface IAlertBreachStore
{
    /// <summary>The saved state; an empty one when there is none. Throws when the file cannot be read.</summary>
    Task<AlertBreachFile> LoadAsync();

    /// <summary>Replaces the saved state.</summary>
    Task SaveAsync(AlertBreachFile state);
}

/// <inheritdoc cref="IAlertBreachStore"/>
public sealed class AlertBreachStore(string path) : IAlertBreachStore
{
    private readonly JsonFileStore<AlertBreachFile> _file = new(path,
        problem: f => f.Thresholds is null || f.HostLoad is null || f.Thresholds.Concat(f.HostLoad).Any(s => s is null) || (f.Containers ?? []).Any(c => c is null) || (f.Gaps ?? []).Any(g => g is null)
            ? "a list of breaches is missing or holds an empty entry" : null);

    public Task<AlertBreachFile> LoadAsync() => _file.LoadAsync();

    public Task SaveAsync(AlertBreachFile state) => _file.SaveAsync(state);
}
