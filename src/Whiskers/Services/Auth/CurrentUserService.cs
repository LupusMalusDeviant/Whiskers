using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>
/// Scoped helper that resolves the current circuit user's email and role.
/// Pages load <see cref="GetRoleAsync"/> once in OnInitializedAsync into a field and use it both to
/// hide UI they may not use and to guard destructive handlers (defense in depth — hiding a button is
/// not by itself authorization). Since R1-26 every call re-checks the session (<see cref="ISessionValidator"/>):
/// a deleted, de-whitelisted or otherwise revoked session resolves to Viewer, and the UI mutation runner refuses it.
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    /// <summary>The email of the synthetic local user <c>Auth:Disabled</c> signs every request in as (Admin); the
    /// middleware in <c>WhiskersPipelineExtensions</c> writes the same literal.</summary>
    public const string AuthDisabledEmail = "local@serverwatch.local";

    private readonly AuthenticationStateProvider _authProvider;
    private readonly IRoleService _roles;
    private readonly ISessionValidator? _sessions;

    public CurrentUserService(AuthenticationStateProvider authProvider, IRoleService roles,
        ISessionValidator? sessions = null)
    {
        _authProvider = authProvider;
        _roles = roles;
        _sessions = sessions;
    }

    public async Task<string?> GetEmailAsync()
    {
        var state = await _authProvider.GetAuthenticationStateAsync();
        return state.User?.FindFirst(ClaimTypes.Email)?.Value;
    }

    public async Task<AppRole> GetRoleAsync()
    {
        var check = await CheckSessionAsync();
        return check.Valid ? check.Role : AppRole.Viewer;
    }

    public async Task<SessionCheck> CheckSessionAsync()
    {
        var state = await _authProvider.GetAuthenticationStateAsync();
        if (_sessions is not null) return await _sessions.CheckAsync(state.User);

        // Without a validator (a container built for one module): the role alone, as before R1-26.
        // Auth-disabled = trusted-LAN full-access mode: the synthetic local user (authenticationType
        // "AuthDisabled") gets Admin, otherwise disabling auth would lock everyone out of Settings.
        if (state.User.Identity?.AuthenticationType == AuthConstants.AuthDisabledScheme)
            return SessionCheck.Allowed(AppRole.Admin);
        return SessionCheck.Allowed(_roles.GetRole(state.User?.FindFirst(ClaimTypes.Email)?.Value));
    }

    /// <summary>True if the current user's role is at least <paramref name="required"/>.</summary>
    public async Task<bool> HasRoleAsync(AppRole required) => (await GetRoleAsync()) >= required;
}
