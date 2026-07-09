using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>Resolves and persists per-user application roles.</summary>
public interface IRoleService
{
    Task InitializeAsync(CancellationToken ct = default);
    AppRole GetRole(string? email);
    bool HasRole(string? email, AppRole requiredRole);
    UserRoleData GetRoleData();
    Task SaveRoleDataAsync(UserRoleData data);
    Task SetRoleAsync(string email, AppRole role);
    Task RemoveRoleAsync(string email);
}
