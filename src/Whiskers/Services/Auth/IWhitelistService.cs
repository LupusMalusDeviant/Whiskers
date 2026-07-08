using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>Manages the email allow-list that gates federated logins.</summary>
public interface IWhitelistService
{
    Task InitializeAsync();
    bool IsEmailAllowed(string? email);
    WhitelistData GetWhitelist();
    Task SaveWhitelistAsync(WhitelistData data);
}
