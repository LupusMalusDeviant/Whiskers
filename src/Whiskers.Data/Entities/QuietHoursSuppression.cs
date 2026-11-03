namespace Whiskers.Services.Persistence;

/// <summary>
/// R1-56: what quiet hours held back for one target in one window of one rule — bounded, durable, and summarized exactly
/// once after the window. <see cref="Id"/> is deterministic (rule, window, target), so a restart during quiet hours adds to
/// the same row instead of starting a second one. At most a fixed number of events are kept as <see cref="ItemsJson"/>; the
/// rest only counts in <see cref="Count"/>, and the summary says "N further events suppressed", so overflow is never silent.
/// </summary>
public class QuietHoursSuppressionEntity
{
    /// <summary><c>rule|window|target</c>.</summary>
    public string Id { get; set; } = "";

    public string RuleId { get; set; } = "";

    /// <summary>The rule's name when the first event was held (the rule may be renamed or removed before the summary).</summary>
    public string RuleName { get; set; } = "";

    /// <summary>The window: the local date on which it started, in the rule's time zone (<c>yyyy-MM-dd</c>).</summary>
    public string WindowKey { get; set; } = "";

    /// <summary>The target the events were about: <c>server/container</c>, as the notification named it.</summary>
    public string Target { get; set; } = "";

    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }

    /// <summary>Every event held for this target in this window, including those not kept in <see cref="ItemsJson"/>.</summary>
    public int Count { get; set; }

    /// <summary>The first events held, as JSON: time, type, title and the redacted detail line (never a secret — the
    /// pipeline redacts before anything is stored).</summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>When the summary was handed to the outbox; null while the window is open or the summary is still to be made.</summary>
    public DateTime? SummarizedAt { get; set; }
}
