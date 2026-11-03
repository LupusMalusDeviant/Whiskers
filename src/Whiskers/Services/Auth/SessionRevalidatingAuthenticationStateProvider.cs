using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>
/// The circuit's authentication state, re-checked (R1-26, INV-09). A Blazor circuit keeps the principal it connected
/// with, and its pages cache the role they loaded; so the circuit checks its session once when the state is set (the
/// role it starts with) and then every <see cref="RevalidationInterval"/> through <see cref="ISessionValidator"/>. A
/// session that is no longer valid, or whose role fell below the role the circuit started with, turns the circuit
/// anonymous: the router shows the sign-in page and no page keeps acting on its cached role. A raised role keeps the
/// circuit. The timer comes from the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class SessionRevalidatingAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    /// <summary>How often a circuit's session is re-checked: with one check in flight, well inside INV-09's 60 s.</summary>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(30);

    private readonly ISessionValidator _sessions;
    private readonly TimeProvider _time;
    private readonly IAuthAuditLog? _audit;
    private readonly ILogger<SessionRevalidatingAuthenticationStateProvider>? _logger;
    private readonly object _gate = new();
    private ITimer? _timer;
    private ClaimsPrincipal? _watched;
    private AppRole? _startRole;
    private bool _disposed;

    public SessionRevalidatingAuthenticationStateProvider(ISessionValidator sessions, TimeProvider? time = null,
        IAuthAuditLog? audit = null, ILogger<SessionRevalidatingAuthenticationStateProvider>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = time ?? TimeProvider.System;
        _audit = audit;
        _logger = logger;
        AuthenticationStateChanged += OnStateChanged;
    }

    private void OnStateChanged(Task<AuthenticationState> state) => _ = WatchAsync(state);

    private async Task WatchAsync(Task<AuthenticationState> stateTask)
    {
        AuthenticationState state;
        try { state = await stateTask; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return; }

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _watched = null;
            _startRole = null;
            if (_disposed || state.User.Identity?.IsAuthenticated != true) return;
            _watched = state.User;
        }
        // The first check fixes the role the circuit starts with; a session already invalid ends right here.
        if (!await RevalidateAsync()) return;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_watched, state.User)) return;
            _timer = _time.CreateTimer(_ => _ = RevalidateAsync(), null, RevalidationInterval, RevalidationInterval);
        }
    }

    /// <summary>Re-checks the watched session now; <c>false</c> when it was ended. Called by the timer; public so a
    /// test can drive it without a timer.</summary>
    public async Task<bool> RevalidateAsync(CancellationToken ct = default)
    {
        ClaimsPrincipal? user;
        lock (_gate) user = _watched;
        if (user is null) return true;

        SessionCheck check;
        try
        {
            check = await _sessions.CheckAsync(user, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogError("A circuit session could not be re-checked ({Error}); it is ended", ex.GetType().Name);
            check = SessionCheck.Revoked(SessionRevocationReasons.StoreUnavailable);
        }

        string reason;
        lock (_gate)
        {
            // Another state was set meanwhile: this answer is about a principal the circuit no longer has.
            if (!ReferenceEquals(_watched, user)) return true;
            if (check.Valid && (_startRole is not { } start || check.Role >= start))
            {
                _startRole ??= check.Role;
                return true;
            }
            reason = check.Valid ? SessionRevocationReasons.RoleLowered : check.Reason ?? SessionRevocationReasons.NotSignedIn;
            _timer?.Dispose();
            _timer = null;
            _watched = null;
            _startRole = null;
        }

        _logger?.LogInformation("A circuit's session was ended ({Reason})", reason);
        if (_audit is not null)
            await _audit.SessionRevokedAsync(null, user.FindFirstValue(ClaimTypes.Email), "circuit", reason);
        SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _watched = null;
        }
        AuthenticationStateChanged -= OnStateChanged;
    }
}
