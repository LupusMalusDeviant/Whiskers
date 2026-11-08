namespace Whiskers.Utils;

/// <summary>R1-57 (INV-40): what counts as a usable deadline for a command on a host, on every transport.</summary>
internal static class ExecutionTimeout
{
    /// <summary>Whether <paramref name="timeout"/> gives a run a real deadline: positive, and finite in the sense
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts (zero, negative and infinite are not).</summary>
    public static bool IsUsable(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= uint.MaxValue - 1.0;
}
