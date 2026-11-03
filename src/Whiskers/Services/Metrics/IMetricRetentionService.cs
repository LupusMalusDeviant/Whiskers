using Whiskers.Configuration;

namespace Whiskers.Services.Metrics;

/// <summary>
/// R1-55: the retention of every metric tier and of the alert history, and its one write path. A key set by the
/// environment or <c>appsettings.json</c> wins and is read-only; the others come from the data directory's
/// <c>app-settings.json</c> or their default. <see cref="SaveAsync"/> is the Settings page's gated, audited
/// <c>config.save</c> (R1-11, R1-22).
/// </summary>
public interface IMetricRetentionService
{
    /// <summary>The retention in effect. Never throws: a value that cannot be read keeps the last valid one (or the
    /// default) with a warning.</summary>
    MetricRetention Current { get; }

    /// <summary>Each setting with its value and whether it is fixed by the environment or <c>appsettings.json</c>.</summary>
    IReadOnlyList<MetricRetentionSetting> Settings { get; }

    /// <summary>Writes the editable values to <c>app-settings.json</c> and publishes them once the file is written. Every
    /// value is checked first (an out-of-range value, or a change to a read-only one, throws
    /// <see cref="ArgumentException"/> and nothing is written); a failed write throws and leaves <see cref="Current"/> as
    /// it was.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveAsync(MetricRetention values, CancellationToken ct = default);
}

/// <summary>One retention setting as the Settings page shows it.</summary>
/// <param name="Source">Where a read-only value comes from (<c>environment / appsettings.json</c>); null when editable.</param>
public sealed record MetricRetentionSetting(MetricRetentionField Field, int Days, bool ReadOnly, string? Source);
