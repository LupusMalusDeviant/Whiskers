using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>
/// The circuit's authentication state, re-checked (R1-26, INV-09). A Blazor circuit keeps the principal it connected
/// with, and its pages cache the role they loaded; so the circuit checks its session once when the state is set (the
/// role it starts with) and then every <see cref="RevalidationInterval"/> through <see cref="ISessionValidator"/>. A
/// session that is no longer valid, or whose role fell below the highest role the circuit has held (a page may have
/// cached it after a promotion), turns the circuit
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
    private AppRole? _highestRole;
    private bool _disposed;
    // n1: each state change gets a number; only the newest one may set what is watched.
    private long _sequence;
    // A role a page was handed before the circuit started watching that principal.
    private (ClaimsPrincipal User, AppRole Role)? _earlyNote;

    public SessionRevalidatingAuthenticationStateProvider(ISessionValidator sessions, TimeProvider? time = null,
        IAuthAuditLog? audit = null, ILogger<SessionRevalidatingAuthenticationStateProvider>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = time ?? TimeProvider.System;
        _audit = audit;
        _logger = logger;
        AuthenticationStateChanged += OnStateChanged;
    }

    private void OnStateChanged(Task<AuthenticationState> state) => _ = WatchAsync(state, Interlocked.Increment(ref _sequence));

    private async Task WatchAsync(Task<AuthenticationState> stateTask, long sequence)
    {
        // Out of the notification that called us: a check that completes synchronously must not set (and announce) the
        // anonymous state while later subscribers still receive the authenticated one.
        await Task.Yield();
        AuthenticationState state;
        try { state = await stateTask; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return; }

        lock (_gate)
        {
            // A newer state was set while this one waited: it alone decides what is watched.
            if (sequence != Interlocked.Read(ref _sequence)) return;
            _timer?.Dispose();
            _timer = null;
            _watched = null;
            _highestRole = null;
            if (_disposed || state.User.Identity?.IsAuthenticated != true) return;
            _watched = state.User;
            if (_earlyNote is { } note && ReferenceEquals(note.User, state.User)) _highestRole = note.Role;
            _earlyNote = null;
        }
        // The first check fixes the role the circuit starts with; a session already invalid ends right here.
        if (!await RevalidateAsync()) return;
        lock (_gate)
        {
            if (_disposed || sequence != Interlocked.Read(ref _sequence) || !ReferenceEquals(_watched, state.User)) return;
            _timer = _time.CreateTimer(_ => _ = RevalidateAsync(), null, RevalidationInterval, RevalidationInterval);
        }
    }

    /// <summary>Records that <paramref name="role"/> was handed to a page of this circuit for <paramref name="user"/>
    /// (<see cref="CurrentUserService"/> calls it on every valid check): the circuit's later checks compare against the
    /// highest role any page may have cached, so a promotion only a page saw, followed by a demotion, still ends it.</summary>
    public void NoteRole(ClaimsPrincipal user, AppRole role)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (_disposed) return;
            if (ReferenceEquals(_watched, user))
            {
                if (_highestRole is not { } highest || role > highest) _highestRole = role;
            }
            else if (_watched is null && (_earlyNote is not { } early || !ReferenceEquals(early.User, user) || role > early.Role))
            {
                _earlyNote = (user, role);
            }
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
            if (check.Valid && (_highestRole is not { } start || check.Role >= start))
            {
                _highestRole = check.Role;   // never lower than before: the highest role this circuit has held
                return true;
            }
            reason = check.Valid ? SessionRevocationReasons.RoleLowered : check.Reason ?? SessionRevocationReasons.NotSignedIn;
            _timer?.Dispose();
            _timer = null;
            _watched = null;
            _highestRole = null;
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
