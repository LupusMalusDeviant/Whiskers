using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;

namespace Whiskers.Services.Docker.Budget;

/// <summary>
/// The per-server concurrency cap (Plan-0001 WP3). One pair of semaphores per server — background and
/// interactive — created on first sight of that server and kept for the process lifetime.
///
/// <para>Defaults are chosen for the smallest machine in the fleet (two cores), not the development box:
/// the incident happened on a two-core Hetzner host, and a limit tuned on an eight-core laptop would have
/// permitted exactly the load that caused it.</para>
/// </summary>
public sealed class ServerBudget : IServerBudget
{
    private static readonly AsyncLocal<bool> IsBackground = new();

    private sealed class Lanes(int background, int interactive)
    {
        public readonly SemaphoreSlim Background = new(background, background);
        public readonly SemaphoreSlim Interactive = new(interactive, interactive);
        public readonly int BackgroundLimit = background;
        public readonly int InteractiveLimit = interactive;
        public int BackgroundInFlight;
        public int InteractiveInFlight;
        public long Started;
        public long WaitedMillisecondsTotal;
        public long MaxWaitMilliseconds;
        public long DiscardedDuplicates;

        /// <summary>Calls whose deadline expired mostly in the queue rather than at the server. A rising
        /// number means Whiskers is the bottleneck — and, unlike a plain timeout, it says so instead of
        /// blaming the host.</summary>
        public long SaturationFailures;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> InFlightKeys = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, Lanes> _lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<ServerBudgetSettings> _settings;
    private readonly ILogger<ServerBudget> _logger;

    /// <summary>Measures the queue wait and the run behind <see cref="Lanes.SaturationFailures"/>. The system clock in
    /// production; tests pass a clock they move by hand, so "waited longer than it ran" does not hang on a runner's
    /// scheduling.</summary>
    private readonly TimeProvider _time;

    public ServerBudget(IOptionsMonitor<ServerBudgetSettings> settings, ILogger<ServerBudget> logger, TimeProvider? time = null)
    {
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public IDisposable BackgroundScope() => EnterBackground();

    /// <summary>
    /// Marks the current async flow as background work without needing an <see cref="IServerBudget"/> to hand.
    ///
    /// <para>The flag behind it is a static <c>AsyncLocal</c> already, so this adds no shared state — it only
    /// removes the reason a caller might skip the scope. <see cref="FleetBackgroundService"/> uses it so that
    /// entering the background lane costs a base class rather than a constructor parameter in every loop.
    /// Ten of twelve loops had skipped it, which put them in the lane reserved for waiting humans.</para>
    /// </summary>
    public static IDisposable EnterBackground() => new Scope();

    public bool IsBackgroundCall => IsBackground.Value;

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous = IsBackground.Value;
        public Scope() => IsBackground.Value = true;
        public void Dispose() => IsBackground.Value = _previous;
    }

    public async Task<T> RunAsync<T>(string serverId, Func<Task<T>> operation, CancellationToken ct = default, string? singleFlightKey = null,
        CancellationToken deadline = default)
    {
        var lanes = LanesFor(serverId);
        var background = IsBackground.Value;
        var gate = background ? lanes.Background : lanes.Interactive;

        // Single-flight applies to background work only. A loop that skips one cycle loses a minute; a
        // person who gets "already running" instead of their logs loses trust in the tool.
        var dedupe = background && singleFlightKey is not null ? $"{serverId}|{singleFlightKey}" : null;
        if (dedupe is not null && !lanes.InFlightKeys.TryAdd(dedupe, 0))
        {
            Interlocked.Increment(ref lanes.DiscardedDuplicates);
            throw new DuplicateRequestException(dedupe);
        }

        var waited = _time.GetTimestamp();
        // The token covers the WAIT, not just the operation: a caller that timed out while queued has to
        // leave the queue, otherwise the backlog outlives the callers and the next slot goes to a ghost.
        // R1-41: a deadline about the call ends the wait too. Running out here is the queue's doing — never the server's,
        // and never mistaken for the caller giving up.
        using var waitLimit = deadline.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, deadline) : null;
        // A deadline already spent before the queue was even reached (the wait for a client used it up) is not this
        // queue's backlog: it still ends the call the same way, but is not counted as saturation.
        var spentBeforeQueue = deadline.IsCancellationRequested;
        try { await gate.WaitAsync(waitLimit?.Token ?? ct); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            if (dedupe is not null) lanes.InFlightKeys.TryRemove(dedupe, out _);
            if (!spentBeforeQueue) Interlocked.Increment(ref lanes.SaturationFailures);
            throw new BudgetSaturatedException(serverId, _time.GetElapsedTime(waited), TimeSpan.Zero, ex);
        }
        catch { if (dedupe is not null) lanes.InFlightKeys.TryRemove(dedupe, out _); throw; }
        var waitMs = (long)_time.GetElapsedTime(waited).TotalMilliseconds;

        Interlocked.Add(ref lanes.WaitedMillisecondsTotal, waitMs);
        Interlocked.Increment(ref lanes.Started);
        long seenMax;
        while (waitMs > (seenMax = Interlocked.Read(ref lanes.MaxWaitMilliseconds)))
            Interlocked.CompareExchange(ref lanes.MaxWaitMilliseconds, waitMs, seenMax);

        if (background) Interlocked.Increment(ref lanes.BackgroundInFlight);
        else Interlocked.Increment(ref lanes.InteractiveInFlight);

        var startedAt = _time.GetTimestamp();
        try
        {
            return await operation();
        }
        // A deadline that expired after the call spent longer queued than running is a statement about this
        // budget, not about the server. Handing the caller a plain timeout here is what let the circuit
        // breaker blame six healthy servers at once for our own backlog.
        // R1-41: never for a call its caller cancelled — that is neither the budget's nor the server's doing.
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException
                                   && !ct.IsCancellationRequested
                                   && _time.GetElapsedTime(startedAt) < TimeSpan.FromMilliseconds(waitMs))
        {
            Interlocked.Increment(ref lanes.SaturationFailures);
            throw new BudgetSaturatedException(
                serverId, TimeSpan.FromMilliseconds(waitMs), _time.GetElapsedTime(startedAt), ex);
        }
        finally
        {
            if (dedupe is not null) lanes.InFlightKeys.TryRemove(dedupe, out _);
            if (background) Interlocked.Decrement(ref lanes.BackgroundInFlight);
            else Interlocked.Decrement(ref lanes.InteractiveInFlight);
            gate.Release();
        }
    }

    public ServerBudgetSnapshot Snapshot(string serverId) => Describe(serverId, LanesFor(serverId));

    public IReadOnlyList<ServerBudgetSnapshot> SnapshotAll() =>
        _lanes.Select(kv => Describe(kv.Key, kv.Value)).OrderBy(s => s.ServerId, StringComparer.Ordinal).ToList();

    private static ServerBudgetSnapshot Describe(string serverId, Lanes l) => new(
        serverId,
        Volatile.Read(ref l.BackgroundInFlight),
        Volatile.Read(ref l.InteractiveInFlight),
        l.BackgroundLimit,
        l.InteractiveLimit,
        Interlocked.Read(ref l.Started),
        Interlocked.Read(ref l.WaitedMillisecondsTotal),
        Interlocked.Read(ref l.MaxWaitMilliseconds),
        Interlocked.Read(ref l.DiscardedDuplicates),
        Interlocked.Read(ref l.SaturationFailures));

    private Lanes LanesFor(string serverId) =>
        _lanes.GetOrAdd(serverId, id =>
        {
            var s = _settings.CurrentValue;
            var (background, interactive) = s.LimitsFor(id);
            _logger.LogInformation(
                "Load budget for server {ServerId}: {Background} concurrent background calls, {Interactive} interactive",
                id, background, interactive);
            return new Lanes(background, interactive);
        });
}
