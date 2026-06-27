using ServerWatch.Models;

namespace ServerWatch.Services.Auth;

/// <summary>Scoped helper that resolves the current circuit user's email and role.</summary>
public interface ICurrentUserService
{
    Task<string?> GetEmailAsync();
    Task<AppRole> GetRoleAsync();
    Task<bool> HasRoleAsync(AppRole required);
}
