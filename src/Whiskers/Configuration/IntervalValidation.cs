using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whiskers.Configuration;

/// <summary>
/// R1-41 (TA-52, F-15): the interval of a background loop is checked at start and wherever it is used, so a zero or
/// negative value is a clear configuration error instead of a loop that spins without a pause or throws, and a value past
/// what <see cref="Task.Delay(TimeSpan)"/> accepts never reaches it.
///
/// <para><b>What refuses the start, what does not.</b> Only a value that never worked — zero or negative (zero spun the
/// loop, a negative one made <see cref="Task.Delay(TimeSpan)"/> throw and stopped the host) — stops the start. A positive
/// value outside the range was accepted by earlier versions (the Settings page allowed any number of hours) and may run
/// in an installation today; refusing it would turn an upgrade into a restart loop with no UI to fix it in. Such a value
/// starts with a warning and is clamped to the range (<see cref="EffectiveInterval{T}"/>).</para>
///
/// <para>A value that becomes invalid later (the file edited, a reload) does not make the options throw — that would end
/// the loop that reads them; the loop keeps its last valid interval and says so (<see cref="IntervalGuard"/>). Saving a
/// value out of range through the settings store is refused (<see cref="ThrowIfInvalid"/>,
/// <see cref="IntervalOutOfRangeException"/>).</para>
/// </summary>
public static class IntervalValidation
{
    /// <summary>Health checks: 5 s to a day.</summary>
    public const int MinHealthSeconds = 5, MaxHealthSeconds = 86_400;

    /// <summary>Metric collection: 5 s to a day.</summary>
    public const int MinMetricsSeconds = 5, MaxMetricsSeconds = 86_400;

    /// <summary>Image-update and CVE checks: an hour to 30 days (720 h) — monthly checks configured before R1-41 keep working,
    /// and the bound stays well below the ~49.7 days <see cref="Task.Delay(TimeSpan)"/> accepts.</summary>
    public const int MinCheckHours = 1, MaxCheckHours = 720;

    /// <summary>Checks one interval of <typeparamref name="T"/> when the host starts (before any hosted service runs): zero or
    /// negative stops the start with an <see cref="OptionsValidationException"/> naming <paramref name="key"/>; a positive
    /// value outside <paramref name="min"/>–<paramref name="max"/> is logged as a warning and clamped.
    ///
    /// <para>It also registers the one <see cref="EffectiveInterval{T}"/> of that interval (in <paramref name="unit"/>s), the
    /// value every consumer uses: the loop that waits it, the exposition that reports against it, the staleness thresholds
    /// derived from it.</para></summary>
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

    /// <summary>A fixed <see cref="EffectiveInterval{T}"/> of the health check, for a monitor built without DI (tests).</summary>
    public static EffectiveInterval<HealthMonitorSettings> HealthCheck(HealthMonitorSettings settings, ILogger logger) =>
        new(() => settings.CheckIntervalSeconds, "HealthMonitor:CheckIntervalSeconds", MinHealthSeconds, MaxHealthSeconds,
            TimeSpan.FromSeconds(1), logger);

    /// <summary>A fixed <see cref="EffectiveInterval{T}"/> of the image-update check, for a checker built without DI (tests).</summary>
    public static EffectiveInterval<ImageUpdateSettings> ImageUpdateCheck(ImageUpdateSettings settings, ILogger logger) =>
        new(() => settings.CheckIntervalHours, "ImageUpdate:CheckIntervalHours", MinCheckHours, MaxCheckHours,
            TimeSpan.FromHours(1), logger);

    /// <summary>The message for a value out of range; null when it is in range.</summary>
    internal static string? Problem(string key, int value, int min, int max) =>
        value >= min && value <= max ? null : $"{key} must be between {min} and {max} (it is {value}).";

    /// <summary>Refuses to save a settings section whose loop interval is out of range (R1-41), with an
    /// <see cref="IntervalOutOfRangeException"/> the Settings page shows as a message.</summary>
    public static void ThrowIfInvalid(object? section)
    {
        var (key, value, min, max) = section switch
        {
            HealthMonitorSettings h => ("HealthMonitor:CheckIntervalSeconds", h.CheckIntervalSeconds, MinHealthSeconds, MaxHealthSeconds),
            MetricsSettings m => ("Metrics:CollectionIntervalSeconds", m.CollectionIntervalSeconds, MinMetricsSeconds, MaxMetricsSeconds),
            ImageUpdateSettings i => ("ImageUpdate:CheckIntervalHours", i.CheckIntervalHours, MinCheckHours, MaxCheckHours),
            CveMonitorSettings c => ("CveMonitor:CheckIntervalHours", c.CheckIntervalHours, MinCheckHours, MaxCheckHours),
            _ => ((string?)null, 0, 0, 0),
        };
        if (key is not null && Problem(key, value, min, max) is { } problem)
            throw new IntervalOutOfRangeException(key, value, min, max, problem);
    }

    internal interface IIntervalCheck
    {
        int Value();
        int Min { get; }
        int Max { get; }
        Type OptionsType { get; }
        string Key { get; }
    }

    private sealed class IntervalCheck<T>(IOptions<T> options, Func<T, int> interval, string key, int min, int max)
        : IIntervalCheck where T : class
    {
        public int Value() => interval(options.Value);
        public int Min => min;
        public int Max => max;
        public Type OptionsType => typeof(T);
        public string Key => key;
    }

    /// <summary>Runs every registered check in <see cref="IHostedLifecycleService.StartingAsync"/>, before the loops start:
    /// zero or negative refuses the start, a positive value out of range is a warning (it is clamped where it is used).</summary>
    internal sealed class IntervalStartupCheck(IEnumerable<IIntervalCheck> checks, ILogger<IntervalStartupCheck>? logger = null)
        : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            foreach (var check in checks)
            {
                var value = check.Value();
                if (value <= 0)
                    throw new OptionsValidationException(check.Key, check.OptionsType,
                    [$"{check.Key} must be greater than zero (it is {value}); the loop would spin or stop the host. " +
                     $"Set it between {check.Min} and {check.Max} — in the data directory's app-settings.json if it was saved there."]);
                if (value < check.Min || value > check.Max)
                    logger?.LogWarning("{Key} is {Value}, outside {Min}–{Max}; {Used} is used until it is changed",
                        check.Key, value, check.Min, check.Max, Math.Clamp(value, check.Min, check.Max));
            }
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>R1-41: a loop interval refused when a settings section is saved; carries what the Settings page needs to say
/// so in the user's language.</summary>
public sealed class IntervalOutOfRangeException(string key, int value, int min, int max, string message)
    : ArgumentOutOfRangeException(key, value, message)
{
    public string Key { get; } = key;
    public int Min { get; } = min;
    public int Max { get; } = max;
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
/// R1-41: the one effective interval of a loop — the <see cref="IntervalGuard"/> value, shared. The loop waits it,
/// <c>/metrics</c> and the MCP summary judge staleness by it, and the self-metrics report it, so a value out of range
/// (say <c>2147483647</c> hours, at start or by a reload) neither overflows a <see cref="TimeSpan"/> anywhere nor makes a
/// threshold disagree with the cadence the loop actually keeps. Registered once per options type by
/// <see cref="IntervalValidation.ValidateInterval{T}"/>; it reads the options live, so a valid saved value applies without
/// a restart.
/// </summary>
public sealed class EffectiveInterval<T> where T : class
{
    private readonly Func<int> _read;
    private readonly IntervalGuard _guard;
    private readonly TimeSpan _unit;
    private readonly Lock _lock = new();

    public EffectiveInterval(IOptionsMonitor<T> options, Func<T, int> read, string key, int min, int max, TimeSpan unit,
        ILogger logger)
        : this(() => read(options.CurrentValue), key, min, max, unit, logger) { }

    internal EffectiveInterval(Func<int> read, string key, int min, int max, TimeSpan unit, ILogger logger)
    {
        _read = read;
        _unit = unit;
        _guard = new IntervalGuard(key, min, max, logger);
    }

    /// <summary>The interval in its unit: the configured value while it is in range, otherwise the last valid one (at
    /// first the value clamped to the range).</summary>
    public int Value
    {
        get
        {
            var configured = _read();
            lock (_lock) return _guard.Get(configured);
        }
    }

    /// <summary><see cref="Value"/> as a <see cref="TimeSpan"/>; always within the validated range.</summary>
    public TimeSpan Interval => _unit * Value;
}
