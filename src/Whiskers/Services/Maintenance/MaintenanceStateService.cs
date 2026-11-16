namespace Whiskers.Services.Maintenance;

/// <summary>In-memory implementation of <see cref="IMaintenanceStateService"/>. Registered as a Core singleton.</summary>
public sealed class MaintenanceStateService : IMaintenanceStateService
{
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _running = new();
    // Signalled whenever registered work ends; the waiter re-checks.
    private TaskCompletionSource _changed = NewSignal();
    // The entry this flow was admitted under (a restore runs inside its own gateway operation): not waited for.
    private readonly AsyncLocal<long?> _current = new();
    private long _next;
    private volatile bool _maintenance;

    public bool IsMaintenance => _maintenance;

    public string? Reason { get; private set; }

    public void EnterMaintenance(string reason)
    {
        lock (_gate)
        {
            Reason = reason;
            _maintenance = true;   // volatile write publishes Reason before the flag flips
        }
    }

    public void ExitMaintenance()
    {
        lock (_gate)
        {
            _maintenance = false;
            Reason = null;
        }
    }

    public IDisposable? TryEnter(string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        lock (_gate)
        {
            if (_maintenance) return null;
            var id = ++_next;
            _running[id] = what;
            _current.Value = id;   // set in a synchronous call: the caller's async flow keeps it
            return new Entry(this, id);
        }
    }

    public IReadOnlyList<string> InFlight
    {
        get { lock (_gate) return _running.OrderBy(r => r.Key).Select(r => r.Value).ToList(); }
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan budget, CancellationToken ct = default)
    {
        var own = _current.Value;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_running.Keys.All(k => k == own)) return true;
                changed = _changed.Task;
            }
            try
            {
                await changed.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;   // the budget is spent
            }
        }
    }

    private void Leave(long id)
    {
        lock (_gate)
        {
            if (!_running.Remove(id)) return;
            var changed = _changed;
            _changed = NewSignal();
            changed.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry(MaintenanceStateService owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave(id);
        }
    }
}
