using System.Globalization;
using System.Text;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

/// <summary>R1-56: turns what quiet hours held back into one summary per window once the window is over.</summary>
public interface IQuietHoursSummarizer
{
    /// <summary>Creates the summary of every window that ended by <paramref name="now"/> — also one that ended early because
    /// its rule was changed, switched off or removed — and hands it to the outbox for each enabled channel.</summary>
    Task SummarizeDueAsync(DateTime now, CancellationToken ct = default);
}

/// <summary>
/// R1-56: one summary per quiet-hours window, created exactly once and delivered at least once, through R1-40's outbox.
///
/// <para>Per window: the summary is added to the outbox under an idempotency key per channel
/// (<see cref="INotificationOutbox.AddOnceAsync"/>), the outbox is written, and only then are the window's rows marked
/// summarized. A stop before the mark leaves the rows pending and the next start adds the summary again — which the
/// outbox refuses, since it holds the key or remembers it. A stop after a channel accepted the summary but before the
/// outbox recorded the delivery sends it again after the restart: Slack, Discord and most other channels have no
/// receiver-side deduplication, so that summary can arrive twice (Matrix deduplicates by its transaction id). Delivered by
/// the outbox worker, it bypasses the per-channel throttle like every redelivery.</para>
/// </summary>
public sealed class QuietHoursSummarizer(
    IAlertSuppressionStore store,
    IQuietHoursService quietHours,
    INotificationOutbox outbox,
    IEnumerable<INotificationChannel> channels,
    ILogger<QuietHoursSummarizer> logger) : IQuietHoursSummarizer
{
    /// <summary>The longest summary text; the events that do not fit are counted, never dropped silently.</summary>
    internal const int MaxText = 1800;

    /// <summary>How long summarized rows are kept.</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly IReadOnlyList<INotificationChannel> _channels = channels.ToList();
    private DateTime _lastPrune;

    public async Task SummarizeDueAsync(DateTime now, CancellationToken ct = default)
    {
        await store.SummarizeAsync(async (pending, token) =>
        {
            var done = new List<string>();
            var made = new List<(string Rule, string Window, int Events, int Channels)>();
            foreach (var window in pending.GroupBy(r => (r.RuleId, r.WindowKey)))
            {
                if (quietHours.IsActive(window.Key.RuleId, window.Key.WindowKey, now)) continue;
                var rows = window.ToList();
                var evt = Summary(rows, now);
                var key = KeyOf(window.Key.RuleId, window.Key.WindowKey);
                var targets = _channels.Where(c => c.Targets.Count > 0).ToList();
                foreach (var channel in targets)
                    await outbox.AddOnceAsync(channel.Name, evt, key + "|" + channel.Name, now);
                done.AddRange(rows.Select(r => r.Id));
                made.Add((rows[0].RuleName, window.Key.WindowKey, rows.Sum(r => r.Count), targets.Count));
            }
            if (done.Count == 0) return done;

            // On disk before the rows are marked: a stop in between re-creates the summary, which the keys refuse.
            if (!await outbox.FlushAsync(token))
            {
                logger.LogWarning("Quiet-hours summaries are kept for delivery but not yet on disk; they are marked once they are");
                return [];
            }
            foreach (var m in made)
                logger.LogInformation("Quiet hours {Rule} ({Window}) held back {Events} notifications; summary queued for {Channels} channels",
                    m.Rule, m.Window, m.Events, m.Channels);
            return done;
        }, now, ct);

        if (now - _lastPrune > TimeSpan.FromHours(1) || now < _lastPrune)
        {
            _lastPrune = now;
            await store.PruneAsync(now - Keep, ct);
        }
    }

    /// <summary>The idempotency key of a window's summary.</summary>
    internal static string KeyOf(string ruleId, string windowKey) => $"quiet-hours|{ruleId}|{windowKey}";

    /// <summary>The summary of one window: per target what was held, oldest first, and how many further events were held
    /// than the text lists.</summary>
    internal static NotificationEvent Summary(IReadOnlyList<QuietHoursSuppressionEntity> rows, DateTime now)
    {
        var total = rows.Sum(r => r.Count);
        var first = rows.Min(r => r.FirstAt);
        var last = rows.Max(r => r.LastAt);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Quiet hours \"{rows[0].RuleName}\" held back {total} {(total == 1 ? "notification" : "notifications")} for " +
            $"{rows.Count} {(rows.Count == 1 ? "target" : "targets")} between {first:yyyy-MM-dd HH:mm} and {last:HH:mm} UTC.");
        var shown = 0;
        var full = false;
        foreach (var row in rows.OrderBy(r => r.FirstAt))
        {
            var header = $"\n• {row.Target} ({row.Count}):";
            if (full || text.Length + header.Length > MaxText) { full = true; continue; }
            text.Append(header);
            foreach (var item in AlertSuppressionStore.Items(row))
            {
                var line = string.Create(CultureInfo.InvariantCulture, $"\n  {item.At:HH:mm} {item.Title}: {item.Detail}");
                if (text.Length + line.Length > MaxText) { full = true; break; }
                text.Append(line);
                shown++;
            }
        }
        if (total > shown)
            text.Append(CultureInfo.InvariantCulture, $"\n… {total - shown} further {(total - shown == 1 ? "event" : "events")} suppressed.");

        return new NotificationEvent
        {
            EventType = NotificationEventTypes.QuietHoursSummary,
            ContainerName = "",
            ImageInfo = text.ToString(),
            Timestamp = now,
            // The same for every channel and attempt (R1-40) and safe in a URL path (Matrix's transaction id).
            DeliveryId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(KeyOf(rows[0].RuleId, rows[0].WindowKey))))[..32].ToLowerInvariant(),
        };
    }
}
