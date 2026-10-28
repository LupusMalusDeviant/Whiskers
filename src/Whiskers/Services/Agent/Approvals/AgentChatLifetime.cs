using Whiskers.Services.Approvals;

namespace Whiskers.Services.Agent.Approvals;

/// <summary>
/// The life of a chat's agent session inside a UI component (R1-15, INV-36). Ending it — the chat is cleared, the
/// page is left, the browser connection is gone for good — first stops the session's run, then cancels its pending
/// approvals. An approval from a chat nobody is watching can then no longer be approved elsewhere and resume the
/// session unattended.
/// </summary>
public sealed class AgentChatLifetime : IAsyncDisposable
{
    private readonly IApprovalStore _approvals;
    private readonly ILogger? _logger;
    private CancellationTokenSource _run = new();
    private string? _sessionId;

    public AgentChatLifetime(IApprovalStore approvals, ILogger? logger = null)
    {
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _logger = logger;
    }

    /// <summary>Passed to the session's runs: cancelled when the lifetime ends.</summary>
    public CancellationToken Token => Volatile.Read(ref _run).Token;

    /// <summary>The session this chat runs; its approvals end with the chat.</summary>
    public void Attach(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Volatile.Write(ref _sessionId, session.SessionId);
    }

    /// <summary>Ends the attached session: its run stops, its pending approvals are cancelled. A new session can be
    /// attached afterwards (a cleared chat starts over).</summary>
    public async Task EndAsync()
    {
        // The run first: a cancelled approval would otherwise read as a rejection and the run would go on.
        var run = Interlocked.Exchange(ref _run, new CancellationTokenSource());
        try { await run.CancelAsync(); }
        catch (AggregateException ex) { _logger?.LogWarning(ex, "Stopping an agent chat run failed"); }

        if (Interlocked.Exchange(ref _sessionId, null) is { } sessionId)
            await _approvals.CancelForSessionAsync(sessionId);
    }

    public async ValueTask DisposeAsync() => await EndAsync();
}
