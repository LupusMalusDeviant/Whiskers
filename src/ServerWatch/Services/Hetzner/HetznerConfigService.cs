using ServerWatch.Configuration;
using ServerWatch.Services.Persistence;
using ServerWatch.Services.Vault;

namespace ServerWatch.Services.Hetzner;

/// <summary>
/// Persists Hetzner integration settings to /app/data/hetzner-config.json.
/// The API token is treated as a secret: when the Vault (VAULT_KEY) is available the token is
/// stored encrypted there and NOT written to the JSON; otherwise it falls back to the JSON file.
/// </summary>
public class HetznerConfigService
{
    private const string TokenVaultKey = "hetzner-api-token";

    private readonly JsonFileStore<HetznerSettings> _store;
    private readonly VaultService _vault;
    private readonly ILogger<HetznerConfigService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HetznerSettings _cached = new();

    public HetznerConfigService(VaultService vault, ILogger<HetznerConfigService> logger)
    {
        _store = new JsonFileStore<HetznerSettings>("/app/data/hetzner-config.json");
        _vault = vault;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            _cached = await _store.LoadAsync();
            _logger.LogInformation("Hetzner config loaded (Enabled={Enabled}, TokenInVault={Vault})",
                _cached.Enabled, _vault.IsEnabled && !string.IsNullOrEmpty(_vault.GetSecret(TokenVaultKey)));
        }
        else
        {
            _logger.LogInformation("No Hetzner config found, integration disabled");
        }
    }

    public HetznerSettings GetSettings() => _cached;

    public bool IsEnabled => _cached.Enabled && !string.IsNullOrWhiteSpace(GetToken());

    /// <summary>True once a token has been stored (vault or plaintext) — used by the settings UI.</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(GetToken());

    public string GetToken()
    {
        if (_vault.IsEnabled)
        {
            var t = _vault.GetSecret(TokenVaultKey);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return _cached.ApiToken ?? "";
    }

    /// <summary>
    /// Saves settings. An empty <see cref="HetznerSettings.ApiToken"/> means "leave the existing
    /// token untouched", so the UI can re-save other fields without re-entering the secret.
    /// </summary>
    public async Task SaveAsync(HetznerSettings settings)
    {
        await _lock.WaitAsync();
        try
        {
            var token = settings.ApiToken?.Trim() ?? "";

            if (string.IsNullOrEmpty(token))
            {
                // Preserve whatever is already stored.
                settings.ApiToken = _vault.IsEnabled ? "" : (_cached.ApiToken ?? "");
            }
            else if (_vault.IsEnabled)
            {
                await _vault.SetSecretAsync(TokenVaultKey, token);
                settings.ApiToken = ""; // never persist plaintext when the vault holds it
            }
            else
            {
                settings.ApiToken = token; // no vault → plaintext fallback
            }

            _cached = settings;
            await _store.SaveAsync(settings);
            _logger.LogInformation("Hetzner config saved (Enabled={Enabled})", settings.Enabled);
        }
        finally
        {
            _lock.Release();
        }
    }
}
