using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>Scoped helper that resolves the current circuit user's email and role.</summary>
public interface ICurrentUserService
{
    Task<string?> GetEmailAsync();
    Task<AppRole> GetRoleAsync();
    Task<bool> HasRoleAsync(AppRole required);

    /// <summary>The current user's session re-checked now (R1-26): whether it is still valid and the role it holds.
    /// A mutating handler relies on this, never on a role the page cached. The default (test doubles) is the role
    /// alone; the real resolver goes through <see cref="ISessionValidator"/>.</summary>
    async Task<SessionCheck> CheckSessionAsync() => SessionCheck.Allowed(await GetRoleAsync());

    /// <summary>Contract 0014 (D2): the credential the user signed in with and the id of the web session
    /// (<see cref="WebSessionClaims"/>). Nulls for what the session does not carry (test doubles by default).</summary>
    Task<(string? CredentialId, string? SessionId)> GetPrincipalFieldsAsync() =>
        Task.FromResult<(string?, string?)>((null, null));
}
