using ServerWatch.Configuration;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Coolify;

public class CoolifyConfigService : ICoolifyConfigService
{
    private readonly JsonFileStore<CoolifySettings> _store;
    private readonly ILogger<CoolifyConfigService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CoolifySettings _cached = new();

    public CoolifyConfigService(ILogger<CoolifyConfigService> logger)
    {
        _store = new JsonFileStore<CoolifySettings>("/app/data/coolify-config.json");
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            _cached = await _store.LoadAsync();
            _logger.LogInformation("Coolify config loaded (Enabled={Enabled}, Url={Url})",
                _cached.Enabled, string.IsNullOrEmpty(_cached.ApiUrl) ? "(not set)" : _cached.ApiUrl);
        }
        else
        {
            _logger.LogInformation("No Coolify config found, integration disabled");
        }
    }

    public CoolifySettings GetSettings() => _cached;

    public bool IsEnabled => _cached.Enabled && !string.IsNullOrWhiteSpace(_cached.ApiUrl);

    public async Task SaveAsync(CoolifySettings settings)
    {
        await _lock.WaitAsync();
        try
        {
            _cached = settings;
            await _store.SaveAsync(settings);
            _logger.LogInformation("Coolify config saved (Enabled={Enabled})", settings.Enabled);
        }
        finally
        {
            _lock.Release();
        }
    }
}
