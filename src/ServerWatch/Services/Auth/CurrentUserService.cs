using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using ServerWatch.Models;

namespace ServerWatch.Services.Auth;

/// <summary>
/// Scoped helper that resolves the current circuit user's email and role.
/// Pages load <see cref="GetRoleAsync"/> once in OnInitializedAsync into a field and use it both to
/// hide UI they may not use and to guard destructive handlers (defense in depth — hiding a button is
/// not by itself authorization).
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly AuthenticationStateProvider _authProvider;
    private readonly IRoleService _roles;

    public CurrentUserService(AuthenticationStateProvider authProvider, IRoleService roles)
    {
        _authProvider = authProvider;
        _roles = roles;
    }

    public async Task<string?> GetEmailAsync()
    {
        var state = await _authProvider.GetAuthenticationStateAsync();
        return state.User?.FindFirst(ClaimTypes.Email)?.Value;
    }

    public async Task<AppRole> GetRoleAsync()
    {
        var state = await _authProvider.GetAuthenticationStateAsync();
        // Auth-disabled = trusted-LAN full-access mode: the synthetic local user (authenticationType
        // "AuthDisabled") gets Admin, otherwise disabling auth would lock everyone out of Settings.
        if (state.User.Identity?.AuthenticationType == "AuthDisabled")
            return AppRole.Admin;
        return _roles.GetRole(state.User?.FindFirst(ClaimTypes.Email)?.Value);
    }

    /// <summary>True if the current user's role is at least <paramref name="required"/>.</summary>
    public async Task<bool> HasRoleAsync(AppRole required) => (await GetRoleAsync()) >= required;
}
