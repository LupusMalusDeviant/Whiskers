using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>
/// R1-55: resolves the metric retention from the configuration layers itself instead of binding the merged section.
/// For each key, a value from any layer but the data directory's <c>app-settings.json</c> (the environment,
/// <c>appsettings.json</c>, …) wins and is read-only — even though <c>app-settings.json</c> is the last layer, a UI save
/// never overrides what the operator set outside Whiskers. Otherwise the stored value applies, otherwise the default. A
/// value that does not parse falls back (with one warning) instead of throwing, and one out of range is clamped: a
/// retention read by a background loop must never stop it (R1-41).
/// </summary>
public sealed class MetricRetentionService : IMetricRetentionService, IDisposable
{
    public const string SectionName = MetricsSettings.SectionName;

    private readonly IConfiguration _configuration;
    private readonly IAppSettingsStore _store;
    private readonly ILogger _logger;
    private readonly string? _appSettingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly IDisposable? _reload;
    private volatile Snapshot _snapshot;

    private sealed record Snapshot(MetricRetention Values, IReadOnlyList<MetricRetentionSetting> Settings);

    /// <param name="appSettingsPath">The UI-written <c>app-settings.json</c>; its values are the editable ones.</param>
    public MetricRetentionService(IConfiguration configuration, IAppSettingsStore store, ILogger<MetricRetentionService> logger,
        string? appSettingsPath)
    {
        _configuration = configuration;
        _store = store;
        _logger = logger;
        _appSettingsPath = appSettingsPath is null ? null : Path.GetFullPath(appSettingsPath);
        _snapshot = Resolve(null);
        // A hand edit of app-settings.json or a changed layer applies without a restart, as before.
        _reload = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public MetricRetention Current => _snapshot.Values;

    public IReadOnlyList<MetricRetentionSetting> Settings => _snapshot.Settings;

    public void Dispose() => _reload?.Dispose();

    private void Reload()
    {
        try { _snapshot = Resolve(_snapshot); }
        catch (Exception ex) { _logger.LogWarning(ex, "Metric retention could not be re-read; the last values stay in use"); }
    }

    public async Task SaveAsync(MetricRetention values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        await _lock.WaitAsync(ct);
        try
        {
            var before = _snapshot;
            // Everything is checked before anything is written.
            foreach (var setting in before.Settings)
            {
                var days = values.Get(setting.Field.Field);
                if (days < MetricRetention.MinDays || days > setting.Field.MaxDays)
                    throw new ArgumentException(
                        $"{setting.Field.Key} must be between {MetricRetention.MinDays} and {setting.Field.MaxDays} days.");
                if (setting.ReadOnly && days != setting.Days)
                    throw new ArgumentException(
                        $"{setting.Field.Key} is set by {setting.Source} and cannot be changed here.");
            }

            var editable = before.Settings.Where(s => !s.ReadOnly).ToList();
            if (editable.Count == 0) return;
            await _store.UpdateSectionAsync(SectionName, stored =>
            {
                var section = stored?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var setting in editable)
                {
                    // Another spelling of the key ("retentiondays") would be the same key to the configuration reader.
                    foreach (var name in section.Select(p => p.Key)
                                 .Where(k => string.Equals(k, setting.Field.Property, StringComparison.OrdinalIgnoreCase)).ToList())
                        section.Remove(name);
                    section[setting.Field.Property] = values.Get(setting.Field.Field);
                }
                return section;
            });
            // Published only once the file is written; a failed write threw above and left the live values as they were.
            var current = before.Values;
            var settings = before.Settings.Select(s =>
            {
                if (s.ReadOnly) return s;
                current = current.With(s.Field.Field, values.Get(s.Field.Field));
                return s with { Days = values.Get(s.Field.Field) };
            }).ToList();
            _snapshot = new Snapshot(current, settings);
        }
        finally { _lock.Release(); }
    }

    private Snapshot Resolve(Snapshot? last)
    {
        var providers = (_configuration as IConfigurationRoot)?.Providers.ToList() ?? [];
        var values = MetricRetention.Default;
        var settings = new List<MetricRetentionSetting>();
        foreach (var field in MetricRetention.Keys)
        {
            string? fixedValue = null, fixedSource = null, stored = null;
            foreach (var provider in providers)
            {
                if (!provider.TryGet(field.Key, out var raw) || raw is null) continue;
                if (IsAppSettings(provider)) stored = raw;
                else { fixedValue = raw; fixedSource = SourceOf(provider, field); }   // the last such layer wins
            }

            var fallback = last?.Values.Get(field.Field) ?? MetricRetention.Default.Get(field.Field);
            var readOnly = fixedValue is not null;
            var days = readOnly ? Parse(field, fixedValue!, fallback)
                : stored is not null ? Parse(field, stored, fallback)
                : MetricRetention.Default.Get(field.Field);
            values = values.With(field.Field, days);
            settings.Add(new MetricRetentionSetting(field, days, readOnly, readOnly ? fixedSource : null));
        }
        return new Snapshot(values, settings);
    }

    private int Parse(MetricRetentionField field, string raw, int fallback)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            WarnOnce(field.Key + "=" + raw, "{Key} = '{Value}' is not a number of days; {Fallback} days stay in use",
                field.Key, raw, fallback);
            return fallback;
        }
        var clamped = Math.Clamp(days, MetricRetention.MinDays, field.MaxDays);
        if (clamped != days)
            WarnOnce(field.Key + "=" + raw, "{Key} = {Value} is outside {Min}–{Max} days; {Clamped} days are used",
                field.Key, days, MetricRetention.MinDays, field.MaxDays, clamped);
        return clamped;
    }

    private void WarnOnce(string what, string message, params object[] args)
    {
        lock (_warned)
            if (!_warned.Add(what)) return;
        _logger.LogWarning(message, args);
    }

    private bool IsAppSettings(IConfigurationProvider provider) =>
        _appSettingsPath is not null
        && provider is FileConfigurationProvider { Source: { Path: { } path, FileProvider: PhysicalFileProvider files } }
        && string.Equals(Path.GetFullPath(Path.Combine(files.Root, path)), _appSettingsPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SourceOf(IConfigurationProvider provider, MetricRetentionField field) => provider switch
    {
        EnvironmentVariablesConfigurationProvider => $"the environment ({field.EnvironmentName})",
        FileConfigurationProvider { Source.Path: { } path } => Path.GetFileName(path),
        _ => provider.GetType().Name,
    };
}
