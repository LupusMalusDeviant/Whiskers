using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Whether a user who owns unattended items still has a way to sign in (R1-04). The whitelist and roles say what a
/// user may do; this says whether the user still exists. A deleted local Identity account ends the user's authority
/// even if a whitelist or role entry was left behind.
/// </summary>
public interface IOwnerAccounts
{
    /// <summary><c>false</c> when <paramref name="email"/> can no longer sign in because its account is gone.</summary>
    bool Exists(string email);

    /// <summary>Records every local account that exists now. Called at startup after the Identity schema is migrated
    /// and whenever a local account is created, so an account is known before it can own anything.</summary>
    void RememberLocalAccounts() { }
}

/// <summary>
/// With local accounts only, the Identity row must exist. With federated sign-in (Google, OIDC) configured as well, a
/// missing row alone proves nothing (federated users have none), so every email ever seen with a local account is
/// remembered in <c>known-local-accounts.json</c>: an owner who had a local account and no longer has one has lost
/// it, whatever else is configured. With authentication disabled there are no accounts at all. Anything that cannot
/// be read counts as "account gone" (fail closed).
/// </summary>
public sealed class OwnerAccounts : IOwnerAccounts
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OwnerAccounts>? _logger;
    private readonly string? _knownPath;
    private readonly bool _authDisabled;
    private readonly bool _localOnly;
    private readonly object _gate = new();

    public OwnerAccounts(IServiceScopeFactory scopes, IConfiguration configuration, DataPathOptions? paths = null,
        ILogger<OwnerAccounts>? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;
        _knownPath = paths?.KnownLocalAccountsJson;
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        var google = !string.IsNullOrWhiteSpace(configuration[$"{GoogleAuthSettings.SectionName}:ClientId"]);
        var oidc = configuration.GetValue<bool>($"{OidcSettings.SectionName}:Enabled")
                   && !string.IsNullOrWhiteSpace(configuration[$"{OidcSettings.SectionName}:Authority"]);
        _localOnly = !_authDisabled && !google && !oidc;
    }

    public bool Exists(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (_authDisabled) return true;
        var normalized = email.Trim().ToUpperInvariant();
        var local = LocalAccounts();
        if (local is null) return false;                 // cannot tell → no authority (fail closed)
        lock (_gate)
        {
            var known = Remember(local);
            if (local.Contains(normalized)) return true;
            if (_localOnly) return false;
            // Federated sign-in is configured too: only an email that once had a local account has lost it.
            return known is not null && !known.Contains(normalized);
        }
    }

    public void RememberLocalAccounts()
    {
        if (LocalAccounts() is not { } local) return;
        lock (_gate) Remember(local);
    }

    /// <summary>The normalized emails of all local accounts, or <c>null</c> when they cannot be read.</summary>
    private HashSet<string>? LocalAccounts()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<WhiskersIdentityDbContext>();
        // No identity store means no local account can exist.
        if (db is null) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null)
                .Select(u => u.NormalizedEmail!).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException
                                       or System.Data.Common.DbException)
        {
            _logger?.LogError(ex, "Local accounts could not be read; owners of unattended items are treated as gone");
            return null;
        }
    }

    /// <summary>Adds the current local accounts to the remembered set and returns it; <c>null</c> when the set
    /// cannot be read or kept (then a missing row in federated mode counts as a deleted account).</summary>
    private HashSet<string>? Remember(HashSet<string> local)
    {
        if (_knownPath is null) return _localOnly ? local : null;
        try
        {
            HashSet<string> known;
            if (Directory.Exists(_knownPath)) throw new IOException("The known-accounts path is a directory.");
            try
            {
                known = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_knownPath))
                        ?? throw new JsonException("The known-accounts file is empty.");
                known = new HashSet<string>(known, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // First start with this record (or it was removed): history begins with the accounts present now.
                // A local account deleted before this point is not known and its owner counts as federated.
                known = new HashSet<string>(StringComparer.Ordinal);
                if (local.Count > 0)
                    _logger?.LogWarning("Starting the record of local accounts at {Path} with {Count} account(s); local " +
                                        "accounts deleted before now are not known to automation ownership",
                        _knownPath, local.Count);
            }
            if (local.IsSubsetOf(known)) return known;
            known.UnionWith(local);
            Directory.CreateDirectory(Path.GetDirectoryName(_knownPath)!);
            var temp = _knownPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(known.Order(StringComparer.Ordinal), Json));
            File.Move(temp, _knownPath, overwrite: true);
            return known;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.LogError(ex, "The known local accounts could not be read or kept at {Path}; owners without a " +
                                  "local account are treated as gone", _knownPath);
            return null;
        }
    }
}
