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
    /// Review round 2: the caller must hold the shared <see cref="AccessControlWriteGate"/> (<paramref name="lease"/>)
    /// across both first-admin writes; every role and whitelist writer takes the same boundary.
    /// </summary>
    Task AddFirstAdminAsync(string email, AccessControlWriteGate.Lease lease);

    /// <summary>
    /// Contract 0017 H2: lists <paramref name="email"/> in an <b>enabled</b> whitelist for the local account
    /// <paramref name="accountId"/> an Admin creates, and records that it did (<see cref="WhitelistData.AddedForAccounts"/>).
    /// Returns false and writes nothing when the whitelist is disabled or the address is listed already. Takes the
    /// <see cref="AccessControlWriteGate"/> (after the caller's AdminGate) and prunes orphan bindings first (contract 0003,
    /// amendment c); any failure throws with nothing written. Not a gateway operation of its own: it runs inside the decided
    /// <c>account.create</c>; its only caller is <c>LocalAccountService.CreateAsync</c> (pinned by MutationGatewayCoverageTests).
    /// </summary>
    Task<bool> AddLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default);

    /// <summary>
    /// Contract 0017 H3: removes the entry <see cref="AddLocalAccountEntryAsync"/> added for <paramref name="accountId"/>,
    /// with the binding prune of contract 0003 rule 5. An address without that account's record (listed before, or
    /// removed or re-added by hand since) stays; returns false then. Runs inside the decided <c>account.delete</c> — or
    /// <c>account.create</c>, to take back the entry of a creation that failed after it was written; its callers are
    /// <c>LocalAccountService.DeleteAsync</c> and the undo of <c>CreateAsync</c> (pinned).
    /// </summary>
    /// <remarks>All three local-account writes throw <see cref="InvalidOperationException"/>, with nothing written, when
    /// they would change the whitelist outside a gateway scope that decided their account operation (INV-46: no
    /// whitelist write without its event). A call that changes nothing needs no decision.</remarks>
    Task<bool> RemoveLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default);

    /// <summary>
    /// Contract 0017 H3: puts back an entry <see cref="RemoveLocalAccountEntryAsync"/> removed for a deletion that then
    /// failed (as <c>RestoreRoleAsync</c> does for the role). An address listed again in between stays a manual entry.
    /// Its only caller is <c>LocalAccountService.DeleteAsync</c> (pinned).
    /// </summary>
    Task RestoreLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default);
}
