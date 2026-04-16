using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Auth;

public class RoleService
{
    private readonly JsonFileStore<UserRoleData> _store;
    private readonly ILogger<RoleService> _logger;
    private UserRoleData _data = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public RoleService(ILogger<RoleService> logger)
    {
        _store = new JsonFileStore<UserRoleData>("/app/data/roles.json");
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            var data = await _store.LoadAsync();
            SetData(data);
            _logger.LogInformation("Loaded {Count} user roles", data.Roles.Count);
        }
        else
        {
            SetData(new UserRoleData());
            _logger.LogInformation("No roles configured, all users get default role: Viewer");
        }
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

    public UserRoleData GetRoleData()
    {
        _lock.EnterReadLock();
        try
        {
            return new UserRoleData
            {
                DefaultRole = _data.DefaultRole,
                Roles = _data.Roles.Select(r => new UserRoleEntry { Email = r.Email, Role = r.Role }).ToList()
            };
        }
        finally { _lock.ExitReadLock(); }
    }

    public async Task SaveRoleDataAsync(UserRoleData data)
    {
        await _store.SaveAsync(data);
        SetData(data);
        _logger.LogInformation("Roles updated: {Count} entries, default={Default}", data.Roles.Count, data.DefaultRole);
    }

    public async Task SetRoleAsync(string email, AppRole role)
    {
        _lock.EnterWriteLock();
        try
        {
            var existing = _data.Roles.FirstOrDefault(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Role = role;
            else
                _data.Roles.Add(new UserRoleEntry { Email = email, Role = role });
        }
        finally { _lock.ExitWriteLock(); }

        await _store.SaveAsync(_data);
    }

    public async Task RemoveRoleAsync(string email)
    {
        _lock.EnterWriteLock();
        try
        {
            _data.Roles.RemoveAll(r => r.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.ExitWriteLock(); }

        await _store.SaveAsync(_data);
    }

    private void SetData(UserRoleData data)
    {
        _lock.EnterWriteLock();
        try { _data = data; }
        finally { _lock.ExitWriteLock(); }
    }
}
