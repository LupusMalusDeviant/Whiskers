using Whiskers.Services.Docker.Budget;

namespace Whiskers.Services;

/// <summary>
/// Base class for every loop that touches the fleet on its own schedule. Its whole job is to put that loop in
/// the background lane of the per-server budget — and to make forgetting impossible.
///
/// <para><b>Why this exists.</b> Plan-0001 gave each server two lanes: four concurrent calls for background
/// loops and four for anything a human is waiting on, kept apart so a long scan cannot freeze the UI. The lane
/// is chosen by an <c>AsyncLocal</c> flag that a caller has to set. On 2026-08-27 exactly two of twelve loops
/// set it. The other ten — metrics, health, CVE, image updates, auto-update, scheduler and four newer ones —
/// counted as interactive and queued in front of the dashboard. The metrics loop alone sweeps every server
/// every 30 seconds and takes 20 seconds to do it, so the interactive lane sat at 4 of 4 on all six servers at
/// once, waits reached five seconds, and calls that waited too long timed out and opened the circuit. The
/// result was a slow dashboard and a stream of "paused / resumed" notifications: the guard against overload
/// had become the source of it.</para>
///
/// <para><b>Why a base class and not a constructor parameter.</b> Ten loops would have needed a new dependency
/// each, and the eleventh — written next month — would still be free to skip it. Here the correct behaviour is
/// the default and <see cref="ExecuteAsync"/> is sealed, so a loop cannot quietly opt out.
/// <c>BackgroundServiceLaneTests</c> holds the line for anything that does not derive from this.</para>
///
/// <para><b>What it deliberately does not cover.</b> The scope wraps the hosted-service loop only. A method
/// that a loop shares with a manual trigger — <c>CveMonitorService.RunOneCycleAsync</c> is the live example —
/// stays interactive when a person calls it, which is right: they are waiting for it.</para>
/// </summary>
public abstract class FleetBackgroundService : BackgroundService
{
    /// <summary>How wide the startup offsets are spread. Twelve loops land roughly two and a half seconds
    /// apart, which is long enough to matter and short enough that nobody notices a loop starting late.</summary>
    internal static readonly TimeSpan StartupSpread = TimeSpan.FromSeconds(30);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Set before the first await so it flows down the whole loop, including anything the loop starts.
        using var background = ServerBudget.EnterBackground();

        // Every loop used to fire in the same second, so a restart put a dozen fleet-wide sweeps into the
        // budget at once, calls timed out waiting, and every server's circuit opened within four seconds —
        // followed by a paused/resumed notification per server. Six deploys in a day made that the loudest
        // thing in the notification list. The work is identical either way; only the pile-up was avoidable.
        var offset = StartupOffsetForTests ?? StartupOffset(GetType());
        if (offset > TimeSpan.Zero)
        {
            try { await Task.Delay(offset, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        await RunAsync(stoppingToken);
    }

    /// <summary>Tests only: replaces the startup offset (null in the app), so a test of the hosted loop does not wait up
    /// to <see cref="StartupSpread"/> for its first cycle.</summary>
    internal TimeSpan? StartupOffsetForTests { get; init; }

    /// <summary>
    /// A stable per-loop startup offset inside <see cref="StartupSpread"/>.
    ///
    /// <para>Derived from the type name rather than drawn at random, so the order is the same on every boot:
    /// a staggered start that shuffles itself is one more thing that behaves differently each time somebody
    /// tries to reproduce a startup problem. <see cref="string.GetHashCode()"/> is deliberately not used — it
    /// is randomised per process, which would make this random with extra steps.</para>
    /// </summary>
    internal static TimeSpan StartupOffset(Type loop)
    {
        var name = loop.Name;
        uint hash = 2166136261;                      // FNV-1a, stable across processes and runs
        foreach (var c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return TimeSpan.FromMilliseconds(hash % (ulong)StartupSpread.TotalMilliseconds);
    }

    /// <summary>The loop body, exactly as <c>ExecuteAsync</c> would have been written.</summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <summary>
    /// R1-41 (TA-52): whether <paramref name="ex"/> is the host stopping this loop — a cancellation while
    /// <paramref name="stoppingToken"/> is cancelled. Only that ends a loop. Every other failure, also a dependency's own
    /// timeout or cancellation (an <c>HttpClient</c> timeout is a <see cref="TaskCanceledException"/>, a linked deadline
    /// an <see cref="OperationCanceledException"/>), is a failed cycle: logged, recorded, and the next cycle runs. A loop
    /// catches with <c>catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))</c>; <c>BackgroundServiceFailureTests</c>
    /// holds every loop to it.
    /// </summary>
    protected internal static bool IsHostStopping(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException && stoppingToken.IsCancellationRequested;
}
