namespace Whiskers.Services.Mutations;

/// <summary>
/// Contract 0023 (batches, safe points): a step that must not be cut in half observes the run's token once, before it
/// starts, and then runs to its end. A command already sent to a host (a <c>docker exec</c>, a migration) is such a
/// step: cancelling it mid-way would leave a half-applied state, so it runs with <see cref="CancellationToken.None"/>.
/// The gateway still ends the run at its deadline (abandoned), whatever the step does.
/// </summary>
public static class SafePoint
{
    /// <summary>Throws when <paramref name="ct"/> is cancelled; otherwise starts <paramref name="step"/> and lets it run
    /// to its end, whatever happens to <paramref name="ct"/> afterwards.</summary>
    public static Task<T> ThenRunToEnd<T>(CancellationToken ct, Func<Task<T>> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ct.ThrowIfCancellationRequested();
        return step();
    }
}
