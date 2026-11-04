using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// The one session check (R1-26). Order: the trusted-LAN <c>Auth:Disabled</c> principal stays Admin exactly as before
/// (only in that mode); any other synthetic identity is refused; a principal without an email is refused; a local
/// password session needs its Identity row (same id, same email) and the security stamp it was issued with; a federated
/// session (R1-27) needs a configured instance and its identity binding, still for the same account; any other
/// authentication type is refused (<see cref="SessionRevocationReasons.BindingMissing"/>); every session needs the
/// whitelist to still allow the email. The role is read from the role store each time. Any error is
/// a refusal (<see cref="SessionRevocationReasons.StoreUnavailable"/>), logged with the exception type only.
/// </summary>
public sealed class SessionValidator : ISessionValidator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWhitelistService _whitelist;
    private readonly IRoleService _roles;
    private readonly ILogger<SessionValidator>? _logger;
    private readonly IIdentityBindingStore? _bindings;
    private readonly bool _authDisabled;

    /// <param name="bindings">R1-27: the identity bindings a federated session is checked against. Without them every
    /// federated session is refused (<see cref="SessionRevocationReasons.StoreUnavailable"/>).</param>
    public SessionValidator(IServiceScopeFactory scopes, IWhitelistService whitelist, IRoleService roles,
        IConfiguration configuration, ILogger<SessionValidator>? logger = null, IIdentityBindingStore? bindings = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        ArgumentNullException.ThrowIfNull(configuration);
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        _logger = logger;
        _bindings = bindings;
    }

    /// <summary>True when the identity bindings are wired (R1-27); the boot tests assert it for the real app.</summary>
    internal bool ChecksBindings => _bindings is not null;

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
        // R1-27: a session is a local password session or a bound federated one. Any other type — every Google/OIDC
        // cookie issued before R1-27 included — has no binding behind it.
        if (authType is not (AuthConstants.LocalPasswordScheme or AuthConstants.FederatedScheme))
            return SessionCheck.Revoked(SessionRevocationReasons.BindingMissing);

        var user = principal!;
        var email = user.FindFirstValue(ClaimTypes.Email);
        // Every sign-in path issues an email claim (the account handle for a federated session).
        if (string.IsNullOrWhiteSpace(email)) return SessionCheck.Revoked(SessionRevocationReasons.NoEmail);

        try
        {
            // Local and federated stay apart (contract 0003, rule 4): a local session is checked against its Identity row
            // and stamp only, a federated one against its binding only.
            if (authType == AuthConstants.LocalPasswordScheme
                && await LocalAccountAsync(user, email, ct) is { } localRefusal)
                return SessionCheck.Revoked(localRefusal);
            if (authType == AuthConstants.FederatedScheme && FederatedBinding(user, email) is { } federatedRefusal)
                return SessionCheck.Revoked(federatedRefusal);

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

    /// <summary>R1-27 (contract 0003, rule 3): <c>null</c> when the session's identity binding still exists and still
    /// belongs to the session's account on a configured instance, the refusal reason otherwise. An unreadable binding
    /// store throws (→ <see cref="SessionRevocationReasons.StoreUnavailable"/>).</summary>
    private string? FederatedBinding(ClaimsPrincipal principal, string email)
    {
        if (!AccessEntries.HasExplicitAdmin(_roles.GetRoleData())) return SessionRevocationReasons.SetupIncomplete;
        var bindingId = principal.FindFirstValue(AuthConstants.BindingIdClaim);
        if (string.IsNullOrEmpty(bindingId)) return SessionRevocationReasons.BindingMissing;
        if (_bindings is null) return SessionRevocationReasons.StoreUnavailable;
        var binding = _bindings.Snapshot().Bindings.FirstOrDefault(b => string.Equals(b.Id, bindingId, StringComparison.Ordinal));
        if (binding is null) return SessionRevocationReasons.BindingRevoked;
        return string.Equals(binding.Account.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : SessionRevocationReasons.BindingRevoked;
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
