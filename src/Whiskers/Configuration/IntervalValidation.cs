using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whiskers.Configuration;

/// <summary>
/// R1-41 (TA-52, F-15): the interval of a background loop is checked at start, so a zero or negative value is a clear
/// configuration error instead of a loop that spins without a pause, and a value past what
/// <see cref="Task.Delay(TimeSpan)"/> accepts is not an exception that stops the host half a day later.
///
/// <para>Only the start is refused. A value that becomes invalid later (the file edited, a reload) does not make the
/// options throw — that would end the loop that reads them; the loop keeps its last valid interval and says so
/// (<see cref="IntervalGuard"/>). Saving an invalid value through the settings store is refused
/// (<see cref="ThrowIfInvalid"/>).</para>
/// </summary>
public static class IntervalValidation
{
    /// <summary>Health checks: 5 s to a day.</summary>
    public const int MinHealthSeconds = 5, MaxHealthSeconds = 86_400;

    /// <summary>Metric collection: 5 s to a day.</summary>
    public const int MinMetricsSeconds = 5, MaxMetricsSeconds = 86_400;

    /// <summary>Image-update and CVE checks: an hour to a week.</summary>
    public const int MinCheckHours = 1, MaxCheckHours = 168;

    /// <summary>Checks one interval of <typeparamref name="T"/> when the host starts (before any hosted service runs);
    /// out of range, the start fails with an <see cref="OptionsValidationException"/> naming <paramref name="key"/>.</summary>
    /// <para>It also registers the one <see cref="EffectiveInterval{T}"/> of that interval (in <paramref name="unit"/>s), the
    /// value every consumer uses while the options can be reloaded: the loop that waits it, the exposition that reports
    /// against it, the staleness thresholds derived from it.</para>
    public static OptionsBuilder<T> ValidateInterval<T>(this OptionsBuilder<T> options, Func<T, int> interval, string key,
        int min, int max, TimeSpan unit) where T : class
    {
        options.Services.AddSingleton<IIntervalCheck>(sp =>
            new IntervalCheck<T>(sp.GetRequiredService<IOptions<T>>(), interval, key, min, max));
        options.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IntervalStartupCheck>());
        options.Services.TryAddSingleton(sp => new EffectiveInterval<T>(sp.GetRequiredService<IOptionsMonitor<T>>(), interval,
            key, min, max, unit, sp.GetService<ILoggerFactory>()?.CreateLogger<EffectiveInterval<T>>()
                                 ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        return options;
    }

    /// <summary>The CVE check interval as the CVE loop uses it — for a consumer outside DI or with the module's
    /// registration missing (then a value out of range is clamped, never converted as it is).</summary>
    public static TimeSpan CveCheckInterval(IServiceProvider? services, CveMonitorSettings current) =>
        services?.GetService<EffectiveInterval<CveMonitorSettings>>()?.Interval
        ?? TimeSpan.FromHours(Math.Clamp(current.CheckIntervalHours, MinCheckHours, MaxCheckHours));

    /// <summary>A fresh <see cref="EffectiveInterval{T}"/> of the CVE check, for a monitor built without DI (tests).</summary>
    public static EffectiveInterval<CveMonitorSettings> CveCheck(IOptionsMonitor<CveMonitorSettings> options, ILogger logger) =>
        new(options, s => s.CheckIntervalHours, "CveMonitor:CheckIntervalHours", MinCheckHours, MaxCheckHours,
            TimeSpan.FromHours(1), logger);

    /// <summary>A fresh <see cref="EffectiveInterval{T}"/> of the metric collection, for a collector built without DI (tests).</summary>
    public static EffectiveInterval<MetricsSettings> MetricsCollection(IOptionsMonitor<MetricsSettings> options, ILogger logger) =>
        new(options, s => s.CollectionIntervalSeconds, "Metrics:CollectionIntervalSeconds", MinMetricsSeconds,
            MaxMetricsSeconds, TimeSpan.FromSeconds(1), logger);

    /// <summary>The message for a value out of range; null when it is in range.</summary>
    internal static string? Problem(string key, int value, int min, int max) =>
        value >= min && value <= max ? null : $"{key} must be between {min} and {max} (it is {value}).";

    /// <summary>Refuses to save a settings section whose loop interval is out of range (R1-41).</summary>
    public static void ThrowIfInvalid(object? section)
    {
        var problem = section switch
        {
            HealthMonitorSettings h => Problem("HealthMonitor:CheckIntervalSeconds", h.CheckIntervalSeconds, MinHealthSeconds, MaxHealthSeconds),
            MetricsSettings m => Problem("Metrics:CollectionIntervalSeconds", m.CollectionIntervalSeconds, MinMetricsSeconds, MaxMetricsSeconds),
            ImageUpdateSettings i => Problem("ImageUpdate:CheckIntervalHours", i.CheckIntervalHours, MinCheckHours, MaxCheckHours),
            CveMonitorSettings c => Problem("CveMonitor:CheckIntervalHours", c.CheckIntervalHours, MinCheckHours, MaxCheckHours),
            _ => null,
        };
        if (problem is not null) throw new ArgumentOutOfRangeException(nameof(section), problem);
    }

    internal interface IIntervalCheck
    {
        string? Problem();
        Type OptionsType { get; }
        string Key { get; }
    }

    private sealed class IntervalCheck<T>(IOptions<T> options, Func<T, int> interval, string key, int min, int max)
        : IIntervalCheck where T : class
    {
        public string? Problem() => IntervalValidation.Problem(key, interval(options.Value), min, max);
        public Type OptionsType => typeof(T);
        public string Key => key;
    }

    /// <summary>Runs every registered check in <see cref="IHostedLifecycleService.StartingAsync"/>, before the loops start.</summary>
    internal sealed class IntervalStartupCheck(IEnumerable<IIntervalCheck> checks) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            foreach (var check in checks)
                if (check.Problem() is { } problem)
                    throw new OptionsValidationException(check.Key, check.OptionsType, [problem]);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>
/// R1-41: the interval a loop uses while its options can change at run time. A value out of range is not used: the loop
/// keeps its last valid interval (at first the clamped value) and logs a warning once per value, so a bad edit is seen
/// and the loop neither spins nor dies.
/// </summary>
public sealed class IntervalGuard(string key, int min, int max, ILogger logger)
{
    private int? _lastValid;
    private int? _warnedFor;

    public int Get(int configured)
    {
        if (configured >= min && configured <= max)
        {
            _lastValid = configured;
            _warnedFor = null;
            return configured;
        }
        var used = _lastValid ?? Math.Clamp(configured, min, max);
        if (_warnedFor != configured)
        {
            _warnedFor = configured;
            logger.LogWarning("{Key} is {Value}, outside {Min}–{Max}; keeping {Used} until it is fixed", key, configured, min, max, used);
        }
        return used;
    }
}

/// <summary>
/// R1-41: the one effective interval of a loop whose options can be reloaded — the <see cref="IntervalGuard"/> value,
/// shared. The loop waits it, <c>/metrics</c> and the MCP summary judge staleness by it, and the self-metrics report it,
/// so a reloaded value out of range (say <c>2147483647</c> hours) neither overflows a <see cref="TimeSpan"/> anywhere nor
/// makes a threshold disagree with the cadence the loop actually keeps. Registered once per options type by
/// <see cref="IntervalValidation.ValidateInterval{T}"/>.
/// </summary>
public sealed class EffectiveInterval<T> where T : class
{
    private readonly IOptionsMonitor<T> _options;
    private readonly Func<T, int> _read;
    private readonly IntervalGuard _guard;
    private readonly TimeSpan _unit;
    private readonly Lock _lock = new();

    public EffectiveInterval(IOptionsMonitor<T> options, Func<T, int> read, string key, int min, int max, TimeSpan unit,
        ILogger logger)
    {
        _options = options;
        _read = read;
        _unit = unit;
        _guard = new IntervalGuard(key, min, max, logger);
    }

    /// <summary>The interval in its unit: the configured value while it is in range, otherwise the last valid one.</summary>
    public int Value
    {
        get
        {
            var configured = _read(_options.CurrentValue);
            lock (_lock) return _guard.Get(configured);
        }
    }

    /// <summary><see cref="Value"/> as a <see cref="TimeSpan"/>; always within the validated range.</summary>
    public TimeSpan Interval => _unit * Value;
}
