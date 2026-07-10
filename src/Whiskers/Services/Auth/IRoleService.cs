using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>Resolves and persists per-user application roles.</summary>
public interface IRoleService
{
    Task InitializeAsync(CancellationToken ct = default);
    AppRole GetRole(string? email);
    bool HasRole(string? email, AppRole requiredRole);
    /// <summary>True if ANY explicit role entry exists (i.e. the instance's access has been configured).
    /// Drives the whitelist's fail-open→fail-closed switch (C5).</summary>
    bool HasAnyRoles();
    /// <summary>True if this email has an EXPLICIT role entry (any role, not the default fallback).</summary>
    bool HasExplicitRole(string? email);
    UserRoleData GetRoleData();
    Task SaveRoleDataAsync(UserRoleData data);
    Task SetRoleAsync(string email, AppRole role);
    Task RemoveRoleAsync(string email);
}
