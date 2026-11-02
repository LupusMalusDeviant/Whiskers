namespace Whiskers.Services.Mutations;

/// <summary>
/// Graceful shutdown of unattended mutating work (R1-39, F-16): once Whiskers starts to stop, no scheduled task,
/// AutoUpdate container or AutoPatch container is started any more; what is already running gets up to
/// <see cref="ShutdownDrain.Budget"/> to finish. What is still running after that is cut off by the stop (a forced
/// stop): its operation stays <c>Running</c> in the journal, its task run stays open, and the next start reconciles
/// both as interrupted — whether it took effect is unknown, and it is not repeated automatically.
/// </summary>
public interface IShutdownDrain
{
    /// <summary>Stopping has begun: start nothing new.</summary>
    bool IsStopping { get; }

    /// <summary>Cancelled when the drain budget is spent: work that can still be cancelled should stop now.</summary>
    CancellationToken Abort { get; }

    /// <summary>
    /// Registers one piece of unattended work as running. Returns <c>null</c> once stopping has begun — the caller
    /// then does not start it. Dispose the handle when the work has ended, also when it failed.
    /// </summary>
    IDisposable? TryEnter(string what);

    /// <summary>What is running right now, oldest first.</summary>
    IReadOnlyList<string> InFlight { get; }
}

/// <inheritdoc cref="IShutdownDrain"/>
/// <remarks>Runs in <see cref="StoppingAsync"/>, which the host calls before it cancels the background loops — so a
/// loop is told to stop starting work before the token it runs on is cancelled, and the work it already started
/// keeps its own resources while it drains. The host's <c>ShutdownTimeout</c> (<see cref="HostTimeout"/>) and the
/// container's grace period (Compose <c>stop_grace_period</c>, Helm <c>terminationGracePeriodSeconds</c>) must be
/// longer than the budget, or the process is killed before the drain ends.</remarks>
public sealed class ShutdownDrain : IShutdownDrain, IHostedLifecycleService
{
    /// <summary>How long running work may take to finish once stopping has begun.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>The host's whole shutdown: the drain, then every hosted service's stop.</summary>
    public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private readonly Dictionary<long, string> _running = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly ILogger<ShutdownDrain> _logger;
    private readonly TimeSpan _budget;
    private TaskCompletionSource _idle = NewIdle(completed: true);
    private long _next;
    private bool _stopping;

    public ShutdownDrain(ILogger<ShutdownDrain> logger) : this(logger, Budget) { }

    internal ShutdownDrain(ILogger<ShutdownDrain> logger, TimeSpan budget)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = budget;
    }

    public bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    public CancellationToken Abort => _abort.Token;

    public IReadOnlyList<string> InFlight
    {
        get { lock (_gate) return _running.OrderBy(r => r.Key).Select(r => r.Value).ToList(); }
    }

    public IDisposable? TryEnter(string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        lock (_gate)
        {
            if (_stopping) return null;
            var id = ++_next;
            if (_running.Count == 0) _idle = NewIdle(completed: false);
            _running[id] = what;
            return new Entry(this, id);
        }
    }

    private void Leave(long id)
    {
        lock (_gate)
        {
            if (_running.Remove(id) && _running.Count == 0) _idle.TrySetResult();
        }
    }

    /// <summary>Stops admitting work and waits for what runs, at most the budget (and never past
    /// <paramref name="ct"/>, the host's own shutdown timeout). Internal for tests.</summary>
    internal async Task DrainAsync(CancellationToken ct)
    {
        Task idle;
        lock (_gate)
        {
            _stopping = true;
            idle = _idle.Task;
            if (_running.Count > 0)
                _logger.LogInformation("Stopping: waiting up to {Budget}s for {Count} running unattended operation(s): {Work}",
                    _budget.TotalSeconds, _running.Count, string.Join(", ", _running.OrderBy(r => r.Key).Select(r => r.Value)));
        }

        try
        {
            await idle.WaitAsync(_budget, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            var left = InFlight;
            _logger.LogWarning("Stopping: {Count} unattended operation(s) did not finish within {Budget}s and are cut off " +
                               "({Work}); the next start reports them as interrupted", left.Count, _budget.TotalSeconds,
                string.Join(", ", left));
        }
        finally
        {
            _abort.Cancel();
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => DrainAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) tcs.TrySetResult();
        return tcs;
    }

    private sealed class Entry(ShutdownDrain drain, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) drain.Leave(id);
        }
    }
}

/// <summary>A drain that admits everything and never stops: for code built outside the host (tests, tools).</summary>
public sealed class NoShutdownDrain : IShutdownDrain
{
    public static readonly NoShutdownDrain Instance = new();
    public bool IsStopping => false;
    public CancellationToken Abort => CancellationToken.None;
    public IDisposable? TryEnter(string what) => Nothing.Handle;
    public IReadOnlyList<string> InFlight => [];

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Handle = new();
        public void Dispose() { }
    }
}
