using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// Which local accounts exist — the inventory the gateway resolves an <c>account.*</c> target against (contract 0012,
/// D12). A read of its own, independent of who is signed in: <see cref="ILocalAccountService.ListAsync"/> answers only an
/// Admin and is scoped to the request, while an account target is resolved for every caller (a Viewer changing their
/// own password included).
/// </summary>
public interface ILocalAccountDirectory
{
    /// <summary>The account's stored email when a local account with <paramref name="email"/> exists (compared
    /// without case), otherwise <c>null</c>. Throws when the accounts cannot be read — never "not found" for a failed
    /// read.</summary>
    Task<string?> FindAsync(string email, CancellationToken ct = default);
}

/// <summary>Reads the identity store in a scope of its own.</summary>
public sealed class LocalAccountDirectory(IServiceScopeFactory scopes) : ILocalAccountDirectory
{
    public async Task<string?> FindAsync(string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        await using var scope = scopes.CreateAsyncScope();
        // No identity store: no local account can exist.
        if (scope.ServiceProvider.GetService<WhiskersIdentityDbContext>() is not { } db) return null;
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
    }
}
