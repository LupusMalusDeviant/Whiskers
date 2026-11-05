using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58: the <c>DiskHealth</c> section of the configuration. Off by default.</summary>
public sealed class DiskHealthSettings
{
    public const string SectionName = "DiskHealth";

    /// <summary>The configuration key of <see cref="Enabled"/>.</summary>
    public const string EnabledKey = SectionName + ":" + nameof(Enabled);

    /// <summary>Whether Whiskers reads disk health (SMART, md, ZFS) from the hosts' metrics endpoints.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// R1-58 (contract 0005): whether disk health is read, and its one write path. <see cref="SaveAsync"/> is the Settings
/// page's gated, audited <c>config.save</c> (R1-11, R1-22): Admin only, refused by the kill switch and by an audit failure,
/// and a failed save leaves the setting as it was. A value set outside <c>app-settings.json</c> (the environment,
/// <c>appsettings.json</c>) wins and is read-only, as for the metric retention (review round 1).
/// </summary>
public interface IDiskHealthSettingsService
{
    /// <summary>The setting in effect. Never throws: a value that does not parse keeps the last valid one (or off).</summary>
    bool Enabled { get; }

    /// <summary>Whether the setting is fixed outside <c>app-settings.json</c> and cannot be changed here.</summary>
    bool ReadOnly { get; }

    /// <summary>Where a read-only value comes from (<c>the environment (DiskHealth__Enabled)</c>); null when editable.</summary>
    string? Source { get; }

    /// <summary>Raised with the new value whenever <see cref="Enabled"/> changes (a save, a reload).</summary>
    event Action<bool>? Changed;

    /// <summary>Writes the setting to <c>app-settings.json</c> and publishes it once the file is written. A change to a
    /// read-only setting throws <see cref="ArgumentException"/> and nothing is written; a failed write throws and leaves
    /// <see cref="Enabled"/> as it was.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveAsync(bool enabled, CancellationToken ct = default);
}

/// <inheritdoc cref="IDiskHealthSettingsService"/>
public sealed class DiskHealthSettingsService : IDiskHealthSettingsService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IAppSettingsStore _store;
    private readonly ILogger _logger;
    private readonly string? _appSettingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable? _reload;
    private volatile Snapshot _snapshot;
    private bool _warned;

    private sealed record Snapshot(bool Enabled, bool ReadOnly, string? Source);

    /// <param name="appSettingsPath">The UI-written <c>app-settings.json</c>; a value from any other layer is read-only.
    /// Null treats every layer as editable.</param>
    public DiskHealthSettingsService(IConfiguration configuration, IAppSettingsStore store, ILogger<DiskHealthSettingsService> logger,
        string? appSettingsPath = null)
    {
        _configuration = configuration;
        _store = store;
        _logger = logger;
        _appSettingsPath = appSettingsPath is null ? null : Path.GetFullPath(appSettingsPath);
        _snapshot = Resolve(fallback: false);
        // A hand edit of app-settings.json or a changed layer applies without a restart, like every other section.
        _reload = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public bool Enabled => _snapshot.Enabled;

    public bool ReadOnly => _snapshot.ReadOnly;

    public string? Source => _snapshot.Source;

    public event Action<bool>? Changed;

    public void Dispose() => _reload?.Dispose();

    private void Reload()
    {
        var before = _snapshot;
        _snapshot = Resolve(before.Enabled);
        if (_snapshot.Enabled != before.Enabled) Raise(_snapshot.Enabled);
    }

    private void Raise(bool enabled)
    {
        try { Changed?.Invoke(enabled); }
        catch (Exception ex) { _logger.LogWarning(ex, "A listener of the disk-health setting failed"); }
    }

    public async Task SaveAsync(bool enabled, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        bool changed;
        try
        {
            var before = _snapshot;
            if (before.ReadOnly && enabled != before.Enabled)
                throw new ArgumentException($"{DiskHealthSettings.EnabledKey} is set by {before.Source} and cannot be changed here.");
            if (before.ReadOnly) return;
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
            _snapshot = before with { Enabled = enabled };
            changed = enabled != before.Enabled;
        }
        finally { _lock.Release(); }
        if (changed) Raise(enabled);
    }

    /// <summary>The value and where it comes from. A value from any layer but <c>app-settings.json</c> wins and is
    /// read-only (the last such layer wins); one that does not parse keeps <paramref name="fallback"/>, warned once.</summary>
    private Snapshot Resolve(bool fallback)
    {
        string? fixedValue = null, fixedSource = null, stored = null;
        if (_configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                if (!provider.TryGet(DiskHealthSettings.EnabledKey, out var raw) || raw is null) continue;
                if (IsAppSettings(provider)) stored = raw;
                else { fixedValue = raw; fixedSource = SourceOf(provider); }
            }
        }
        else stored = _configuration[DiskHealthSettings.EnabledKey];

        var readOnly = fixedValue is not null;
        var raw2 = readOnly ? fixedValue : stored;
        var value = raw2 is null ? false : Parse(raw2, fallback);
        return new Snapshot(value, readOnly, readOnly ? fixedSource : null);
    }

    private bool Parse(string raw, bool fallback)
    {
        if (bool.TryParse(raw.Trim(), out var value))
        {
            _warned = false;
            return value;
        }
        if (!_warned)
        {
            _warned = true;
            _logger.LogWarning("{Key} = '{Value}' is not true or false; disk health stays {State} until it is fixed",
                DiskHealthSettings.EnabledKey, raw, fallback ? "on" : "off");
        }
        return fallback;
    }

    private bool IsAppSettings(IConfigurationProvider provider) =>
        _appSettingsPath is null
        || provider is FileConfigurationProvider { Source: { Path: { } path, FileProvider: PhysicalFileProvider files } }
           && string.Equals(Path.GetFullPath(Path.Combine(files.Root, path)), _appSettingsPath,
               OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SourceOf(IConfigurationProvider provider) => provider switch
    {
        EnvironmentVariablesConfigurationProvider => "the environment (DiskHealth__Enabled)",
        FileConfigurationProvider { Source.Path: { } path } => Path.GetFileName(path),
        _ => provider.GetType().Name,
    };
}
