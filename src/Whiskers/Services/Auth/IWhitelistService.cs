using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>Manages the email allow-list that gates federated logins.</summary>
public interface IWhitelistService
{
    Task InitializeAsync(CancellationToken ct = default);
    bool IsEmailAllowed(string? email);
    WhitelistData GetWhitelist();
    [Whiskers.Services.Mutations.MutatingOperation("config.whitelist")]
    Task SaveWhitelistAsync(WhitelistData data);
}
