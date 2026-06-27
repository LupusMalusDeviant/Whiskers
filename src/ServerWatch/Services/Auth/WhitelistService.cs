using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Auth;

public class WhitelistService : IWhitelistService
{
    private readonly JsonFileStore<WhitelistData> _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhitelistService> _logger;
    private WhitelistData _cached = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public WhitelistService(IConfiguration configuration, ILogger<WhitelistService> logger)
    {
        _store = new JsonFileStore<WhitelistData>("/app/data/whitelist.json");
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync()
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

        _lock.EnterReadLock();
        try
        {
            if (!_cached.Enabled || _cached.Emails.Count == 0)
                return true;

            return _cached.Emails.Contains(email, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.ExitReadLock();
        }
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
        await _store.SaveAsync(data);
        SetCache(data);
        _logger.LogInformation("Whitelist updated: {Count} emails, enabled={Enabled}", data.Emails.Count, data.Enabled);
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
