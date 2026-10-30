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
    // R1-22: one writer at a time from capture to diff, so a save's recorded change is the change it made.
    private readonly SemaphoreSlim _save = new(1, 1);

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
        await _save.WaitAsync();
        try
        {
            var before = GetRoleData();
            EnsureAdminRemains(before, copy);
            await _store.SaveAsync(copy);
            SetData(copy);
            Whiskers.Services.Mutations.ConfigChange.Record("roles", before, copy);   // R1-22
        }
        finally { _save.Release(); }
        _logger.LogInformation("Roles updated: {Count} entries, default={Default}", copy.Roles.Count, copy.DefaultRole);
    }

    public async Task SetRoleAsync(string email, AppRole role)
    {
        await _save.WaitAsync();
        try
        {
            UserRoleData snapshot, before;
            _lock.EnterWriteLock();
            try
            {
                before = Snapshot(_data);
                var next = Snapshot(_data);
                var existing = next.Roles.FirstOrDefault(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    existing.Role = role;
                else
                    next.Roles.Add(new UserRoleEntry { Email = email, Role = role });
                EnsureAdminRemains(_data, next);
                _data = next;
                // Snapshot inside the lock; persist outside — never serialize the live list another writer may mutate.
                snapshot = Snapshot(_data);
            }
            finally { _lock.ExitWriteLock(); }

            await _store.SaveAsync(snapshot);
            Whiskers.Services.Mutations.ConfigChange.Record("roles", before, snapshot);   // R1-22
        }
        finally { _save.Release(); }
    }

    public async Task RemoveRoleAsync(string email)
    {
        await _save.WaitAsync();
        try
        {
            UserRoleData snapshot, before;
            _lock.EnterWriteLock();
            try
            {
                before = Snapshot(_data);
                var next = Snapshot(_data);
                next.Roles.RemoveAll(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                EnsureAdminRemains(_data, next);
                _data = next;
                snapshot = Snapshot(_data);
            }
            finally { _lock.ExitWriteLock(); }

            await _store.SaveAsync(snapshot);
            Whiskers.Services.Mutations.ConfigChange.Record("roles", before, snapshot);   // R1-22
        }
        finally { _save.Release(); }
    }

    /// <summary>Once an instance has an explicit Admin it must keep one. Without an explicit Admin the first-run
    /// wizard counts as not completed, so removing or demoting the last Admin re-opened setup on the next start
    /// (audit TA-02). Only explicit entries count — a default role of Admin is not an administrator anyone chose.</summary>
    private static void EnsureAdminRemains(UserRoleData current, UserRoleData next)
    {
        if (current.Roles.Any(r => r.Role == AppRole.Admin) && !next.Roles.Any(r => r.Role == AppRole.Admin))
            throw new InvalidOperationException("At least one user must keep the Admin role.");
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
