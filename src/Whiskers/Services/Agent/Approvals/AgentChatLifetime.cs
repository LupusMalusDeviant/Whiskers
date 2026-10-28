using Whiskers.Services.Approvals;

namespace Whiskers.Services.Agent.Approvals;

/// <summary>
/// The life of a chat's agent session inside a UI component (R1-15, INV-36). Ending it — the chat is cleared or
/// closed, the page is left, the browser connection drops — stops the current run and cancels the session's pending
/// approvals. An approval from a chat nobody is watching can then no longer be approved elsewhere and resume the
/// session unattended.
/// <para>A send takes its run token with <see cref="BeginRun"/> before its first <c>await</c>, and hands it to
/// <see cref="Attach"/> and <see cref="AfterRaiseAsync"/>: a chat ended while the send was still preparing (reading
/// the page, taking a screenshot, starting the session) refuses it, and nothing of it runs. The next message begins
/// a new run.</para>
/// </summary>
public sealed class AgentChatLifetime : IAsyncDisposable
{
    private readonly IApprovalStore _approvals;
    private readonly IChatConnectionWatcher? _connection;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private CancellationTokenSource _run = new();
    private string? _sessionId;
    private bool _disposed;

    public AgentChatLifetime(IApprovalStore approvals, IChatConnectionWatcher? connection = null, ILogger? logger = null)
    {
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _logger = logger;
        _connection = connection;
        if (_connection is not null) _connection.ConnectionDown += EndAsync;
    }

    /// <summary>The token of the run a send is about to start; take it before the send's first <c>await</c>. It is
    /// cancelled when the chat ends; once the chat is disposed it is cancelled already.</summary>
    public CancellationToken BeginRun()
    {
        lock (_gate)
            return _disposed ? new CancellationToken(canceled: true) : _run.Token;
    }

    /// <summary>Ties <paramref name="session"/> to the chat for the run <paramref name="run"/> (from
    /// <see cref="BeginRun"/>). Throws <see cref="OperationCanceledException"/> when that run was ended in the
    /// meantime or the chat is disposed: the send must then not go on.</summary>
    public void Attach(IAgentSession session, CancellationToken run)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_disposed || run.IsCancellationRequested || run != _run.Token)
                throw new OperationCanceledException("The chat ended before this run started.", run);
            _sessionId = session.SessionId;
        }
    }

    /// <summary>Called right after an approval was raised for <paramref name="session"/> in the run
    /// <paramref name="run"/>: if the chat ended in between, the approval is cancelled at once.</summary>
    public async Task AfterRaiseAsync(IAgentSession session, CancellationToken run)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (run.IsCancellationRequested) await _approvals.CancelForSessionAsync(session.SessionId);
    }

    /// <summary>Ends the current run and cancels the attached session's pending approvals. The chat can go on
    /// afterwards with a new run.</summary>
    public Task EndAsync() => EndAsync(dispose: false);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) _connection.ConnectionDown -= EndAsync;
        await EndAsync(dispose: true);
    }

    private async Task EndAsync(bool dispose)
    {
        string? sessionId;
        lock (_gate)
        {
            // The run first, under the lock: a send that took this run's token can no longer attach, and a cancelled
            // approval cannot read as a rejection to a run that goes on.
            var run = _run;
            _run = new CancellationTokenSource();
            if (dispose) _disposed = true;
            try { run.Cancel(); }
            catch (AggregateException ex) { _logger?.LogWarning(ex, "Stopping an agent chat run failed"); }
            sessionId = _sessionId;
            _sessionId = null;
        }
        if (sessionId is not null) await _approvals.CancelForSessionAsync(sessionId);
    }
}
