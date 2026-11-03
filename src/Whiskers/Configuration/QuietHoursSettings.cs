using System.Globalization;

namespace Whiskers.Configuration;

/// <summary>
/// R1-56: quiet hours — windows in which notifications are held back from the channels and delivered as one summary
/// afterwards. Evaluation never pauses: the in-app feed and <c>AlertHistory</c> keep recording, and AI triggers are
/// dispatched as always (a trigger decides for itself). Stored in <c>app-settings.json</c> (section <c>QuietHours</c>) and
/// written only through the Settings page's gated, audited <c>config.save</c>.
/// </summary>
public sealed class QuietHoursSettings
{
    public const string SectionName = "QuietHours";

    /// <summary>At most this many rules.</summary>
    public const int MaxRules = 20;

    public List<QuietHoursRule> Rules { get; set; } = [];
}

/// <summary>One quiet-hours window: daily from <see cref="Start"/> to <see cref="End"/> on the wall clock of
/// <see cref="TimeZone"/>.</summary>
public sealed class QuietHoursRule
{
    /// <summary>Stable identity: the suppressed events of a window are grouped under it, and a renamed rule keeps its window.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>An IANA time zone (<c>Europe/Berlin</c>, <c>UTC</c>), explicit — never the server's own zone.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Start of the window, <c>HH:mm</c> local time (inclusive).</summary>
    public string Start { get; set; } = "22:00";

    /// <summary>End of the window, <c>HH:mm</c> local time (exclusive). Earlier than <see cref="Start"/>: the window
    /// crosses midnight.</summary>
    public string End { get; set; } = "07:00";

    /// <summary>The event types this rule holds (a breach type covers its <c>_recovered</c>, <c>log_alert</c> every log
    /// alert). Empty: a global rule — every suppressible event.</summary>
    public List<string> EventTypes { get; set; } = [];

    public QuietHoursRule Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, TimeZone = TimeZone, Start = Start, End = End,
        EventTypes = [.. EventTypes],
    };
}

/// <summary>
/// R1-56: when a <see cref="QuietHoursRule"/> is in effect. On the local wall clock of the rule's IANA time zone, so a
/// window keeps its local times across daylight-saving changes: on the night the clocks go forward a 02:00–03:00 window
/// does not occur at all and a 01:00–04:00 window lasts two hours; on the night they go back, the repeated hour is inside
/// a window that covers it, both times.
/// </summary>
public static class QuietHoursWindow
{
    /// <summary>The time zone named by <paramref name="id"/>, when it is an IANA id this system knows; otherwise null. A
    /// Windows zone name is refused even on Windows: the rule must mean the same everywhere.</summary>
    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id is "UTC" or "Etc/UTC") return TimeZoneInfo.Utc;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            if (zone.HasIanaId) return zone;
            // Windows resolves IANA ids too, but reports the zone under its Windows name: accept only what converts back.
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _) ? zone : null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A <c>HH:mm</c> time of day, or null.</summary>
    public static TimeSpan? Time(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
            ? t : null;

    /// <summary>Why <paramref name="rule"/> cannot be saved, or null when it can.</summary>
    public static string? Problem(QuietHoursRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || rule.Id.Contains('|'))
            return "A quiet-hours rule needs an id of at most 64 characters without '|'.";
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 100)
            return "A quiet-hours rule needs a name of at most 100 characters.";
        if (Resolve(rule.TimeZone) is null)
            return $"'{rule.TimeZone}' is not an IANA time zone this system knows (for example Europe/Berlin or UTC).";
        if (Time(rule.Start) is not { } start) return $"'{rule.Start}' is not a time of day (HH:mm).";
        if (Time(rule.End) is not { } end) return $"'{rule.End}' is not a time of day (HH:mm).";
        if (start == end) return "A quiet-hours window must not start and end at the same time.";
        foreach (var type in rule.EventTypes)
            if (!Whiskers.Services.Notifications.NotificationEventTypes.SuppressibleEvents.Contains(type)
                && type != Whiskers.Services.Notifications.NotificationEventTypes.LogAlertPrefix)
                return $"'{type}' is no event type quiet hours can hold.";
        return null;
    }

    /// <summary>The window of <paramref name="rule"/> in effect at <paramref name="utcNow"/>, named by the local date it
    /// started on (<c>yyyy-MM-dd</c>); null outside the window, for a disabled rule and for one that cannot be read.</summary>
    public static string? Active(QuietHoursRule rule, DateTime utcNow)
    {
        if (!rule.Enabled || Resolve(rule.TimeZone) is not { } zone
            || Time(rule.Start) is not { } start || Time(rule.End) is not { } end || start == end)
            return null;

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        var at = local.TimeOfDay;
        DateTime? started = start < end
            ? at >= start && at < end ? local.Date : null
            : at >= start ? local.Date : at < end ? local.Date.AddDays(-1) : null;   // crosses midnight
        return started?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
