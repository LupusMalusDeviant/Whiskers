using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Whether a user who owns unattended items still has a way to sign in (R1-04). The whitelist and roles say what a
/// user may do; this says whether the user still exists. When local accounts are the only way in, a deleted local
/// Identity account ends the user's authority even if a whitelist or role entry was left behind.
/// </summary>
public interface IOwnerAccounts
{
    /// <summary><c>false</c> when <paramref name="email"/> can no longer sign in because its account is gone.</summary>
    bool Exists(string email);
}

/// <summary>Federated sign-in (Google, OIDC) creates no local row, so the whitelist alone decides there; with local
/// accounts only, the Identity row must exist. With authentication disabled there are no accounts at all.</summary>
public sealed class OwnerAccounts : IOwnerAccounts
{
    private readonly IServiceScopeFactory _scopes;
    private readonly bool _localOnly;

    public OwnerAccounts(IServiceScopeFactory scopes, IConfiguration configuration)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        ArgumentNullException.ThrowIfNull(configuration);
        var authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        var google = !string.IsNullOrWhiteSpace(configuration[$"{Configuration.GoogleAuthSettings.SectionName}:ClientId"]);
        var oidc = configuration.GetValue<bool>($"{Configuration.OidcSettings.SectionName}:Enabled")
                   && !string.IsNullOrWhiteSpace(configuration[$"{Configuration.OidcSettings.SectionName}:Authority"]);
        _localOnly = !authDisabled && !google && !oidc;
    }

    public bool Exists(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!_localOnly) return true;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<WhiskersIdentityDbContext>();
        // No identity store means no local account can exist either: the owner cannot sign in.
        if (db is null) return false;
        var normalized = email.Trim().ToUpperInvariant();
        try
        {
            return db.Users.AsNoTracking().Any(u => u.NormalizedEmail == normalized);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException
                                       or System.Data.Common.DbException)
        {
            return false;   // cannot tell → no authority (fail closed)
        }
    }
}
