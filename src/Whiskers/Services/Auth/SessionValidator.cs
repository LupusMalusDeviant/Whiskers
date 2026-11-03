using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// The one session check (R1-26). Order: the trusted-LAN <c>Auth:Disabled</c> principal stays Admin exactly as before
/// (only in that mode); any other synthetic identity is refused; a principal without an email is refused; a local
/// password session needs its Identity row (same id, same email) and the security stamp it was issued with; every
/// session needs the whitelist to still allow the email. The role is read from the role store each time. Any error is
/// a refusal (<see cref="SessionRevocationReasons.StoreUnavailable"/>), logged with the exception type only.
/// </summary>
public sealed class SessionValidator : ISessionValidator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWhitelistService _whitelist;
    private readonly IRoleService _roles;
    private readonly ILogger<SessionValidator>? _logger;
    private readonly bool _authDisabled;

    public SessionValidator(IServiceScopeFactory scopes, IWhitelistService whitelist, IRoleService roles,
        IConfiguration configuration, ILogger<SessionValidator>? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        ArgumentNullException.ThrowIfNull(configuration);
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        _logger = logger;
    }

    public async Task<SessionCheck> CheckAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        var identity = principal?.Identity;
        if (identity is not { IsAuthenticated: true }) return SessionCheck.Revoked(SessionRevocationReasons.NotSignedIn);

        var authType = identity.AuthenticationType;
        // Trusted-LAN mode keeps its behaviour: the synthetic local user is Admin by that mode, never by a cookie.
        if (authType == AuthConstants.AuthDisabledScheme)
            return _authDisabled
                ? SessionCheck.Allowed(AppRole.Admin)
                : SessionCheck.Revoked(SessionRevocationReasons.SyntheticIdentity);
        if (authType == AuthConstants.AgentSyntheticScheme)
            return SessionCheck.Revoked(SessionRevocationReasons.SyntheticIdentity);

        var email = principal!.FindFirstValue(ClaimTypes.Email);
        // Every sign-in path issues an email claim (the whitelist gate refuses a federated login without one).
        if (string.IsNullOrWhiteSpace(email)) return SessionCheck.Revoked(SessionRevocationReasons.NoEmail);

        try
        {
            if (authType == AuthConstants.LocalPasswordScheme
                && await LocalAccountAsync(principal, email, ct) is { } localRefusal)
                return SessionCheck.Revoked(localRefusal);

            if (!_whitelist.IsEmailAllowed(email)) return SessionCheck.Revoked(SessionRevocationReasons.NotWhitelisted);
            return SessionCheck.Allowed(_roles.GetRole(email));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The type only: a message may carry an email or a connection string.
            _logger?.LogError("A session could not be re-checked ({Error}); it is ended", ex.GetType().Name);
            return SessionCheck.Revoked(SessionRevocationReasons.StoreUnavailable);
        }
    }

    /// <summary><c>null</c> when the local account still stands behind the session, the refusal reason otherwise.</summary>
    private async Task<string?> LocalAccountAsync(ClaimsPrincipal principal, string email, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = principal.FindFirstValue(AuthConstants.SecurityStampClaim);
        // A cookie issued before R1-26 carries no stamp: it cannot be tied to the account as it is now.
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(stamp)) return SessionRevocationReasons.StampMismatch;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<WhiskersIdentityDbContext>();
        if (db is null) return SessionRevocationReasons.StoreUnavailable;
        var row = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.SecurityStamp })
            .FirstOrDefaultAsync(ct);
        if (row is null) return SessionRevocationReasons.AccountDeleted;
        if (!string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase)) return SessionRevocationReasons.AccountChanged;
        if (string.IsNullOrEmpty(row.SecurityStamp) || !string.Equals(row.SecurityStamp, stamp, StringComparison.Ordinal))
            return SessionRevocationReasons.StampMismatch;
        return null;
    }
}
