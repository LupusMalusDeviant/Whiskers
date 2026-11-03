using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// One-time recovery codes for local accounts (R1-49). A code is 24 symbols from a 32-symbol alphabet (120 random bits
/// from <see cref="RandomNumberGenerator"/>), shown in groups of four. It is stored only as an ASP.NET Identity password
/// hash in the account's Identity token row (<c>AspNetUserTokens</c>, provider <see cref="TokenProvider"/>, name
/// <see cref="TokenName"/>) together with its expiry — no schema change, and the SQLite→PostgreSQL migrator copies the
/// table. Issuing a code removes the account's password, clears its lockout and gives it a new security stamp in one
/// save, so every existing session ends (R1-26) and the old password no longer signs in.
/// </summary>
public static class RecoveryCodes
{
    public const string TokenProvider = "Whiskers";
    public const string TokenName = "RecoveryCode";

    /// <summary>How long a code can be used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // No 0/O and 1/I: a code read out over the phone survives.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int Length = 24;
    private const string Version = "v1";

    /// <summary>A new random code, formatted <c>XXXX-XXXX-…</c>.</summary>
    public static string Generate()
    {
        var raw = RandomNumberGenerator.GetString(Alphabet, Length);
        return string.Join('-', Enumerable.Range(0, Length / 4).Select(i => raw.Substring(i * 4, 4)));
    }

    /// <summary>The code as it is hashed: upper case, without separators and white space.</summary>
    public static string Normalize(string? code) =>
        new((code ?? "").Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    internal static string Protect(IPasswordHasher<AppUser> hasher, AppUser user, string code, DateTimeOffset expiresUtc) =>
        $"{Version};{expiresUtc.ToUnixTimeSeconds()};{hasher.HashPassword(user, Normalize(code))}";

    /// <summary>Reads a stored value; <c>false</c> for anything not written by <see cref="Protect"/>.</summary>
    internal static bool TryRead(string? value, out DateTimeOffset expiresUtc, out string hash)
    {
        expiresUtc = default;
        hash = "";
        var parts = value?.Split(';', 3);
        if (parts is not [Version, var expires, var stored] || string.IsNullOrEmpty(stored)
            || !long.TryParse(expires, out var seconds))
            return false;
        try { expiresUtc = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        hash = stored;
        return true;
    }

    internal static Task<IdentityUserToken<string>?> FindAsync(WhiskersIdentityDbContext db, string userId, CancellationToken ct) =>
        db.UserTokens.FirstOrDefaultAsync(t => t.UserId == userId && t.LoginProvider == TokenProvider && t.Name == TokenName, ct);

    /// <summary>
    /// Issues a fresh code for <paramref name="user"/> (tracked by <paramref name="db"/>, the context of
    /// <paramref name="users"/>' store): replaces an earlier code, removes the password, clears the lockout and changes
    /// the security stamp — one save, guarded by the account's concurrency stamp. On failure nothing is changed and the
    /// code is <c>null</c>.
    /// </summary>
    internal static async Task<(IdentityResult Result, string? Code, DateTimeOffset ExpiresUtc)> IssueAsync(
        UserManager<AppUser> users, WhiskersIdentityDbContext db, AppUser user, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var code = Generate();
        var expires = nowUtc + Lifetime;
        var value = Protect(users.PasswordHasher, user, code, expires);
        var row = await FindAsync(db, user.Id, ct);
        if (row is null)
            db.UserTokens.Add(new IdentityUserToken<string>
            {
                UserId = user.Id, LoginProvider = TokenProvider, Name = TokenName, Value = value,
            });
        else
            row.Value = value;
        user.PasswordHash = null;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        IdentityResult result;
        try
        {
            result = await users.UpdateSecurityStampAsync(user);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
        if (!result.Succeeded)
        {
            // Nothing of the half-made change may ride along with a later save in this scope.
            db.ChangeTracker.Clear();
            return (result, null, default);
        }
        return (result, code, expires);
    }
}
