using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>Whether a user owner still has a way to sign in.</summary>
public enum OwnerAccountState
{
    /// <summary>A local account exists, or the user signs in federated (or authentication is off).</summary>
    Present,
    /// <summary>The user had a local account and it is gone.</summary>
    Deleted,
    /// <summary>No local account, and Whiskers has not seen the user sign in federated yet: it could be a local
    /// account deleted before the record began. Refused (fail closed) until the user signs in or an admin takes
    /// over.</summary>
    Unverified,
}

/// <summary>
/// Whether a user who owns unattended items still has a way to sign in (R1-04). The whitelist and roles say what a
/// user may do; this says whether the user still exists. A deleted local Identity account ends the user's authority
/// even if a whitelist or role entry was left behind.
/// </summary>
public interface IOwnerAccounts
{
    /// <summary><c>false</c> when <paramref name="email"/> cannot be shown to still have a way to sign in.</summary>
    bool Exists(string email);

    /// <summary>The account state of <paramref name="email"/>; tells a deleted from an unverified account.</summary>
    OwnerAccountState StateOf(string email) => Exists(email) ? OwnerAccountState.Present : OwnerAccountState.Deleted;

    /// <summary>Records every local account that exists now. Called at startup after the Identity schema is migrated
    /// and whenever a local account is created, so an account is known before it can own anything.</summary>
    void RememberLocalAccounts() { }

    /// <summary>Records that <paramref name="email"/> was seen signed in through Google or OIDC.</summary>
    void RememberFederated(string email) { }
}

/// <summary>
/// With local accounts only, the Identity row must exist. With federated sign-in (Google, OIDC) configured as well, a
/// missing row alone proves nothing, so <c>known-local-accounts.json</c> records every email seen with a local
/// account and every email seen signed in federated. Without a local row an owner counts as present only once seen
/// signed in federated; one that had a local account has lost it; anything else is unverified. With authentication
/// disabled there are no accounts at all. Anything that cannot be read counts as "not present" (fail closed).
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

    /// <summary>The persisted record, as normalized emails.</summary>
    private sealed record Known(HashSet<string> Local, HashSet<string> Federated);

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

    public bool Exists(string email) => StateOf(email) == OwnerAccountState.Present;

    public OwnerAccountState StateOf(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (_authDisabled) return OwnerAccountState.Present;
        var normalized = Normalize(email);
        var local = LocalAccounts();
        if (local is null) return OwnerAccountState.Unverified;          // cannot tell → fail closed
        lock (_gate)
        {
            var known = Update(k => k.Local.UnionWith(local));
            if (local.Contains(normalized)) return OwnerAccountState.Present;
            if (_localOnly || known?.Local.Contains(normalized) == true) return OwnerAccountState.Deleted;
            // Federated sign-in is configured too: present only once seen signed in that way.
            return known?.Federated.Contains(normalized) == true
                ? OwnerAccountState.Present
                : OwnerAccountState.Unverified;
        }
    }

    public void RememberLocalAccounts()
    {
        if (LocalAccounts() is not { } local) return;
        lock (_gate) Update(k => k.Local.UnionWith(local));
    }

    public void RememberFederated(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (_authDisabled || _localOnly) return;
        var normalized = Normalize(email);
        lock (_gate) Update(k => k.Federated.Add(normalized));
    }

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();

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
            _logger?.LogError(ex, "Local accounts could not be read; owners of unattended items are treated as unverified");
            return null;
        }
    }

    /// <summary>Applies <paramref name="change"/> to the record, writes it when it changed and returns it; <c>null</c>
    /// when the record cannot be read or kept (then nothing counts as known). Call under <see cref="_gate"/>.</summary>
    private Known? Update(Action<Known> change)
    {
        if (_knownPath is null) return null;
        try
        {
            var (known, existed) = Read();
            var before = (known.Local.Count, known.Federated.Count);
            change(known);
            if (existed && (known.Local.Count, known.Federated.Count) == before) return known;
            Directory.CreateDirectory(Path.GetDirectoryName(_knownPath)!);
            var temp = _knownPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                local = known.Local.Order(StringComparer.Ordinal),
                federated = known.Federated.Order(StringComparer.Ordinal),
            }, Json));
            File.Move(temp, _knownPath, overwrite: true);
            return known;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            _logger?.LogError(ex, "The record of local and federated accounts could not be read or kept at {Path}; " +
                                  "owners without a local account are treated as unverified", _knownPath);
            return null;
        }
    }

    private (Known Known, bool Existed) Read()
    {
        if (Directory.Exists(_knownPath)) throw new IOException("The known-accounts path is a directory.");
        string text;
        try
        {
            text = File.ReadAllText(_knownPath!);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // First start with this record (or it was removed): owners without a local account stay unverified
            // until they sign in again or an admin takes their items over.
            _logger?.LogWarning("Starting the record of local and federated accounts at {Path}; owners without a " +
                                "local account are refused until they sign in or an admin takes their items over",
                _knownPath);
            return (new Known(new(StringComparer.Ordinal), new(StringComparer.Ordinal)), false);
        }
        using var doc = JsonDocument.Parse(text);
        HashSet<string> Set(string name) =>
            doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var list)
                && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Select(e => e.GetString() ?? throw new JsonException("The known-accounts file holds a null email."))
                    .ToHashSet(StringComparer.Ordinal)
                : throw new JsonException($"The known-accounts file has no '{name}' list.");
        return (new Known(Set("local"), Set("federated")), true);
    }
}
