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
    [Whiskers.Services.Mutations.MutatingOperation("config.roles")]
    Task SaveRoleDataAsync(UserRoleData data);
    [Whiskers.Services.Mutations.MutatingOperation("config.roles")]
    Task SetRoleAsync(string email, AppRole role);
    [Whiskers.Services.Mutations.MutatingOperation("config.roles")]
    Task RemoveRoleAsync(string email);

    /// <summary>
    /// The first-run wizard's one role write (R1-48, option a): makes <paramref name="email"/> the first Admin. It runs
    /// before any person exists whom the Mutation Gateway could authorize, so it is not a gateway operation; instead it is
    /// as narrow as it can be. It refuses (<see cref="InvalidOperationException"/>, nothing written) as soon as any explicit
    /// Admin exists — checked and written under the store's own write lock, so of two concurrent calls at most one
    /// creates an Admin. Its only caller is <c>SetupStateService.CompleteSetupAsync</c>, after the setup token was
    /// verified (pinned by <c>MutationGatewayCoverageTests</c>); the wizard audits it (<c>setup.first_admin</c>).
    /// Review round 2: the caller must hold the shared <see cref="AccessControlWriteGate"/> (<paramref name="lease"/>),
    /// the boundary every role and whitelist writer takes, so no writer runs between the check and the write or between
    /// the wizard's whitelist and role writes. The Admin becomes visible to readers only after it was persisted.
    /// </summary>
    Task SetFirstAdminAsync(string email, AccessControlWriteGate.Lease lease);
}
