using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

public class RoleService : IRoleService, IInitializable
{
    private readonly JsonFileStore<UserRoleData> _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RoleService> _logger;
    private UserRoleData _data = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public RoleService(IConfiguration configuration, ILogger<RoleService> logger, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        _store = new JsonFileStore<UserRoleData>(storePath ?? (dataPaths ?? DataPathOptions.Default).RolesJson);
        _configuration = configuration;
        _logger = logger;
    }

    public int Order => 20;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_store.Exists())
        {
            var data = await _store.LoadAsync();
            SetData(data);
            _logger.LogInformation("Loaded {Count} user roles", data.Roles.Count);
        }
        else
        {
            // C5 admin bootstrap: a fresh instance (no roles.json yet) must never end up admin-less — else
            // nobody can open the Admin-gated Settings and no one can ever be promoted. Seed the configured
            // admin email(s) as Admin so whoever set WHISKERS_ADMIN_EMAIL / GOOGLE_ADMIN_EMAIL can manage the
            // instance. Only ever on first run; an existing roles.json is loaded above and NEVER overwritten.
            var data = new UserRoleData();
            foreach (var adminEmail in ResolveBootstrapAdminEmails())
                data.Roles.Add(new UserRoleEntry { Email = adminEmail, Role = AppRole.Admin });

            if (data.Roles.Count > 0)
            {
                await _store.SaveAsync(data);
                SetData(data);
                _logger.LogInformation("Bootstrapped {Count} admin role(s) from configuration (no roles.json existed)", data.Roles.Count);
            }
            else
            {
                SetData(data);
                _logger.LogInformation("No roles configured, all users get default role: Viewer");
            }
        }
    }

    // Admin emails to seed on first run: the provider-neutral WHISKERS_ADMIN_EMAIL plus the first
    // GoogleAuth:AllowedEmails entry (= the documented GOOGLE_ADMIN_EMAIL). De-duped, case-insensitive.
    private IEnumerable<string> ResolveBootstrapAdminEmails()
    {
        var emails = new List<string>();
        var whiskersAdmin = _configuration["WHISKERS_ADMIN_EMAIL"];
        if (!string.IsNullOrWhiteSpace(whiskersAdmin)) emails.Add(whiskersAdmin.Trim());
        var googleAdmin = _configuration.GetSection("GoogleAuth:AllowedEmails").Get<List<string>>()?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(googleAdmin)) emails.Add(googleAdmin.Trim());
        return emails.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get the role for a user email. Returns DefaultRole if not explicitly assigned.</summary>
    public AppRole GetRole(string? email)
    {
        if (string.IsNullOrEmpty(email)) return AppRole.Viewer;

        _lock.EnterReadLock();
        try
        {
            var entry = _data.Roles.FirstOrDefault(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            return entry?.Role ?? _data.DefaultRole;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Check if the user has at least the required role level.</summary>
    public bool HasRole(string? email, AppRole requiredRole)
    {
        var userRole = GetRole(email);
        return (int)userRole >= (int)requiredRole;
    }

    public bool HasAnyRoles()
    {
        _lock.EnterReadLock();
        try { return _data.Roles.Count > 0; }
        finally { _lock.ExitReadLock(); }
    }

    public bool HasExplicitRole(string? email)
    {
        if (string.IsNullOrEmpty(email)) return false;
        _lock.EnterReadLock();
        try { return _data.Roles.Any(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase)); }
        finally { _lock.ExitReadLock(); }
    }

    public UserRoleData GetRoleData()
    {
        _lock.EnterReadLock();
        try { return Snapshot(_data); }
        finally { _lock.ExitReadLock(); }
    }

    public async Task SaveRoleDataAsync(UserRoleData data)
    {
        // Clone before persisting/caching so the caller's mutable object can't alias the enforcement state.
        var copy = Snapshot(data);
        await _store.SaveAsync(copy);
        SetData(copy);
        _logger.LogInformation("Roles updated: {Count} entries, default={Default}", copy.Roles.Count, copy.DefaultRole);
    }

    public async Task SetRoleAsync(string email, AppRole role)
    {
        UserRoleData snapshot;
        _lock.EnterWriteLock();
        try
        {
            var existing = _data.Roles.FirstOrDefault(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Role = role;
            else
                _data.Roles.Add(new UserRoleEntry { Email = email, Role = role });
            // Snapshot inside the lock; persist outside — never serialize the live list another writer may mutate.
            snapshot = Snapshot(_data);
        }
        finally { _lock.ExitWriteLock(); }

        await _store.SaveAsync(snapshot);
    }

    public async Task RemoveRoleAsync(string email)
    {
        UserRoleData snapshot;
        _lock.EnterWriteLock();
        try
        {
            _data.Roles.RemoveAll(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            snapshot = Snapshot(_data);
        }
        finally { _lock.ExitWriteLock(); }

        await _store.SaveAsync(snapshot);
    }

    // Deep copy: cached/persisted state can never be aliased by a caller's mutable object, and
    // serialization never enumerates a list another writer is concurrently mutating.
    private static UserRoleData Snapshot(UserRoleData src) => new()
    {
        DefaultRole = src.DefaultRole,
        Roles = src.Roles.Select(r => new UserRoleEntry { Email = r.Email, Role = r.Role }).ToList()
    };

    private void SetData(UserRoleData data)
    {
        _lock.EnterWriteLock();
        try { _data = data; }
        finally { _lock.ExitWriteLock(); }
    }
}
