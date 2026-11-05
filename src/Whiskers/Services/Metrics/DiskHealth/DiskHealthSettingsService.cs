using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58: the <c>DiskHealth</c> section of the configuration. Off by default.</summary>
public sealed class DiskHealthSettings
{
    public const string SectionName = "DiskHealth";

    /// <summary>Whether Whiskers reads disk health (SMART, md, ZFS) from the hosts' metrics endpoints.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// R1-58 (contract 0005): whether disk health is read, and its one write path. <see cref="SaveAsync"/> is the Settings
/// page's gated, audited <c>config.save</c> (R1-11, R1-22): Admin only, refused by the kill switch and by an audit failure,
/// and a failed save leaves the setting as it was.
/// </summary>
public interface IDiskHealthSettingsService
{
    /// <summary>The setting in effect. Never throws: a section that does not bind keeps the last valid value (or off).</summary>
    bool Enabled { get; }

    /// <summary>Writes the setting to <c>app-settings.json</c> and publishes it once the file is written. A failed write
    /// throws and leaves <see cref="Enabled"/> as it was.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveAsync(bool enabled, CancellationToken ct = default);
}

/// <inheritdoc cref="IDiskHealthSettingsService"/>
public sealed class DiskHealthSettingsService : IDiskHealthSettingsService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IAppSettingsStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable? _reload;
    private volatile bool _enabled;
    private bool _warned;

    public DiskHealthSettingsService(IConfiguration configuration, IAppSettingsStore store, ILogger<DiskHealthSettingsService> logger)
    {
        _configuration = configuration;
        _store = store;
        _logger = logger;
        _enabled = Read(fallback: false);
        // A hand edit of app-settings.json applies without a restart, like every other section.
        _reload = ChangeToken.OnChange(configuration.GetReloadToken, () => _enabled = Read(_enabled));
    }

    public bool Enabled => _enabled;

    public void Dispose() => _reload?.Dispose();

    public async Task SaveAsync(bool enabled, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _store.UpdateSectionAsync(DiskHealthSettings.SectionName, stored =>
            {
                var section = stored?.DeepClone() as JsonObject ?? new JsonObject();
                // Another spelling of the key ("enabled") would be the same key to the configuration reader.
                foreach (var name in section.Select(p => p.Key)
                             .Where(k => string.Equals(k, nameof(DiskHealthSettings.Enabled), StringComparison.OrdinalIgnoreCase)).ToList())
                    section.Remove(name);
                section[nameof(DiskHealthSettings.Enabled)] = enabled;
                return section;
            });
            // Published only once the file is written; a failed write threw above and left the setting as it was.
            _enabled = enabled;
        }
        finally { _lock.Release(); }
    }

    private bool Read(bool fallback)
    {
        try
        {
            var value = _configuration.GetSection(DiskHealthSettings.SectionName).Get<DiskHealthSettings>()?.Enabled ?? false;
            _warned = false;
            return value;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning("The DiskHealth section cannot be read ({Error}); disk health stays {State} until app-settings.json is fixed",
                    ex.Message, fallback ? "on" : "off");
            }
            return fallback;
        }
    }
}
