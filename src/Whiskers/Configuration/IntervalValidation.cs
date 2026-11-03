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
    public static OptionsBuilder<T> ValidateInterval<T>(this OptionsBuilder<T> options, Func<T, int> interval, string key,
        int min, int max) where T : class
    {
        options.Services.AddSingleton<IIntervalCheck>(sp =>
            new IntervalCheck<T>(sp.GetRequiredService<IOptions<T>>(), interval, key, min, max));
        options.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IntervalStartupCheck>());
        return options;
    }

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
