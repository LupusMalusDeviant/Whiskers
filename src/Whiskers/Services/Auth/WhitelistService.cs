using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

public class WhitelistService : IWhitelistService, IInitializable
{
    private readonly JsonFileStore<WhitelistData> _store;
    private readonly IConfiguration _configuration;
    private readonly IRoleService _roles;
    private readonly ILogger<WhitelistService> _logger;
    private WhitelistData _cached = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public WhitelistService(IConfiguration configuration, IRoleService roleService, ILogger<WhitelistService> logger, DataPathOptions? dataPaths = null)
    {
        _store = new JsonFileStore<WhitelistData>((dataPaths ?? DataPathOptions.Default).WhitelistJson);
        _configuration = configuration;
        _roles = roleService;
        _logger = logger;
    }

    public int Order => 10;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_store.Exists())
        {
            var data = await _store.LoadAsync();
            SetCache(data);
            _logger.LogInformation("Loaded whitelist: {Count} emails, enabled={Enabled}", data.Emails.Count, data.Enabled);
        }
        else
        {
            // Seed from config if AllowedEmails are set
            var configEmails = _configuration.GetSection("GoogleAuth:AllowedEmails").Get<List<string>>();
            if (configEmails is { Count: > 0 })
            {
                var data = new WhitelistData { Enabled = true, Emails = configEmails };
                await _store.SaveAsync(data);
                SetCache(data);
                _logger.LogInformation("Seeded whitelist from config: {Count} emails", configEmails.Count);
            }
            else
            {
                SetCache(new WhitelistData());
                _logger.LogInformation("No whitelist configured, all Google users allowed");
            }
        }
    }

    public bool IsEmailAllowed(string? email)
    {
        if (string.IsNullOrEmpty(email))
            return false;

        bool enabled, inList;
        int count;
        _lock.EnterReadLock();
        try
        {
            enabled = _cached.Enabled;
            count = _cached.Emails.Count;
            inList = enabled && _cached.Emails.Contains(email, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Enabled = enforce the list. Enabled-but-empty = deny all (an admin who cleared the last entry must
        // not silently open the instance to every Google account).
        if (enabled)
            return count > 0 && inList;

        // Whitelist disabled (never configured). C5: previously an unconditional fail-open ("allow everyone").
        // Now fail-open ONLY while the instance is otherwise unconfigured (no role entries) — preserving the
        // legacy behaviour for existing deployments that rely on it. As soon as ANY role exists (e.g. the C5
        // admin bootstrap seeded one), a disabled whitelist is fail-CLOSED: only users with an explicit role
        // are admitted, so a fresh instance with a configured admin no longer lets every Google/OIDC account in.
        return !_roles.HasAnyRoles() || _roles.HasExplicitRole(email);
    }

    public WhitelistData GetWhitelist()
    {
        _lock.EnterReadLock();
        try
        {
            return new WhitelistData
            {
                Enabled = _cached.Enabled,
                Emails = new List<string>(_cached.Emails)
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public async Task SaveWhitelistAsync(WhitelistData data)
    {
        // Deep-copy before persisting/caching so the enforcement snapshot never aliases a caller-owned
        // list (e.g. the Settings page's live edit buffer). Otherwise later, unsaved UI edits would take
        // effect immediately and a concurrent mutation could throw during the auth-path re-check.
        var copy = new WhitelistData
        {
            Enabled = data.Enabled,
            Emails = new List<string>(data.Emails)
        };
        await _store.SaveAsync(copy);
        SetCache(copy);
        _logger.LogInformation("Whitelist updated: {Count} emails, enabled={Enabled}", copy.Emails.Count, copy.Enabled);
    }

    private void SetCache(WhitelistData data)
    {
        _lock.EnterWriteLock();
        try
        {
            _cached = data;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
