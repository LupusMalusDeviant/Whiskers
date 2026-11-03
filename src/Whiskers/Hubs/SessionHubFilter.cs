using Microsoft.AspNetCore.SignalR;
using Whiskers.Services.Auth;

namespace Whiskers.Hubs;

/// <summary>
/// R1-26 (INV-09): a hub connection is authenticated once, when it connects; a deleted, de-whitelisted or otherwise
/// revoked user's open connection would otherwise keep answering. Every hub method call re-checks the connection's
/// principal through <see cref="ISessionValidator"/>; a revoked one is refused and the connection closed. Fail closed:
/// a check that throws refuses too. Hub-level only — the HTTP middleware order is untouched.
/// </summary>
public sealed class SessionHubFilter : IHubFilter
{
    private readonly ISessionValidator _sessions;
    private readonly ILogger<SessionHubFilter>? _logger;

    public SessionHubFilter(ISessionValidator sessions, ILogger<SessionHubFilter>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger;
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
            _logger?.LogInformation("A hub connection's session was ended ({Reason})", check.Reason);
            connection.Abort();
            throw new HubException("The session has ended. Sign in again.");
        }
        return await next(invocationContext);
    }
}
