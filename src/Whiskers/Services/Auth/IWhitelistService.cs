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

    /// <summary>
    /// The first-run wizard's one whitelist write (R1-48, option a): adds <paramref name="email"/> to the list (no other
    /// change; whether the list is enforced stays as it is). Not a gateway operation, because it runs before any person
    /// exists whom the gateway could authorize; it refuses (<see cref="InvalidOperationException"/>, nothing written) as
    /// soon as any explicit Admin exists. Its only caller is <c>SetupStateService.CompleteSetupAsync</c>, after the setup
    /// token was verified and before the first Admin's role is written (pinned by <c>MutationGatewayCoverageTests</c>).
    /// </summary>
    Task AddFirstAdminAsync(string email);
}
