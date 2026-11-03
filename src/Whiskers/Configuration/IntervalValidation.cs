using Microsoft.Extensions.Options;

namespace Whiskers.Configuration;

/// <summary>
/// R1-41 (TA-52, F-15): the interval of a background loop is checked when the configuration is bound, so a zero or
/// negative value is a clear configuration error at start instead of a loop that spins without a pause, and a value past
/// what <see cref="Task.Delay(TimeSpan)"/> accepts is not an exception that stops the host half a day later.
/// </summary>
public static class IntervalValidation
{
    /// <summary>Health checks: 5 s to a day.</summary>
    public const int MinHealthSeconds = 5, MaxHealthSeconds = 86_400;

    /// <summary>Metric collection: 5 s to a day.</summary>
    public const int MinMetricsSeconds = 5, MaxMetricsSeconds = 86_400;

    /// <summary>Image-update and CVE checks: an hour to a week.</summary>
    public const int MinCheckHours = 1, MaxCheckHours = 168;

    /// <summary>Validates one interval of <typeparamref name="T"/> on start and on every reload.</summary>
    public static OptionsBuilder<T> ValidateInterval<T>(this OptionsBuilder<T> options, Func<T, int> interval, string key,
        int min, int max) where T : class
        => options
            .Validate(s => interval(s) >= min && interval(s) <= max, $"{key} must be between {min} and {max}.")
            .ValidateOnStart();
}
