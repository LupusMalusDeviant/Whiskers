using ServerWatch.Models;

namespace ServerWatch.Services.Auth;

/// <summary>Resolves and persists per-user application roles.</summary>
public interface IRoleService
{
    Task InitializeAsync();
    AppRole GetRole(string? email);
    bool HasRole(string? email, AppRole requiredRole);
    UserRoleData GetRoleData();
    Task SaveRoleDataAsync(UserRoleData data);
    Task SetRoleAsync(string email, AppRole role);
    Task RemoveRoleAsync(string email);
}
