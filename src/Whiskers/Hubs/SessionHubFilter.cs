using Microsoft.AspNetCore.SignalR;
using Whiskers.Services.Auth;

namespace Whiskers.Hubs;

/// <summary>
/// R1-26 (INV-09): a hub connection is authenticated once, when it connects. This filter (on
/// <see cref="ContainerHub"/> only) re-checks the connection's principal through <see cref="ISessionValidator"/> on every
/// hub method call — a revoked one is refused, the connection closed and the revocation audited (surface <c>hub</c>) —
/// and registers every open connection with <see cref="HubSessionSweep"/>, which closes a revoked connection that never
/// calls anything (it would otherwise keep receiving the server's broadcasts). Fail closed: a check that throws refuses.
/// Hub-level only — the HTTP middleware order is untouched.
/// </summary>
public sealed class SessionHubFilter : IHubFilter
{
    private readonly ISessionValidator _sessions;
    private readonly HubSessionSweep _sweep;
    private readonly ILogger<SessionHubFilter>? _logger;

    public SessionHubFilter(ISessionValidator sessions, HubSessionSweep sweep, ILogger<SessionHubFilter>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _logger = logger;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        _sweep.Track(context.Context);
        await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        _sweep.Forget(context.Context.ConnectionId);
        await next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);
        var connection = invocationContext.Context;
        SessionCheck check;
        try
        {
            check = await _sessions.CheckAsync(connection.User, connection.ConnectionAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError("A hub call's session could not be re-checked ({Error}); it is refused", ex.GetType().Name);
            check = SessionCheck.Revoked(SessionRevocationReasons.StoreUnavailable);
        }
        if (!check.Valid)
        {
            await _sweep.EndAsync(connection, check.Reason ?? SessionRevocationReasons.NotSignedIn);
            throw new HubException("The session has ended. Sign in again.");
        }
        return await next(invocationContext);
    }
}
