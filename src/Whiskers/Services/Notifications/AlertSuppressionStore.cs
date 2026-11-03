using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

/// <summary>One event quiet hours held, as the summary lists it.</summary>
public sealed record SuppressedItem(DateTime At, string EventType, string Title, string Detail);

/// <summary>
/// R1-56: the durable record of what quiet hours held back — one row per rule, window and target
/// (<see cref="QuietHoursSuppressionEntity"/>), bounded to <see cref="MaxItems"/> events with a count of the rest. It
/// survives a restart during quiet hours; the summarizer reads it after the window and marks it summarized once the summary
/// is in the outbox.
/// </summary>
public interface IAlertSuppressionStore
{
    /// <summary>Records <paramref name="evt"/> (already redacted) as held by <paramref name="match"/>. Returns false when it
    /// was not recorded — the window's summary for this target was already made — and the caller delivers the event
    /// instead. Throws when the database cannot be written; the caller delivers then too (an alert is never lost to a
    /// failed hold).</summary>
    Task<bool> HoldAsync(QuietHoursMatch match, NotificationEvent evt, CancellationToken ct = default);

    /// <summary>Hands every row still to be summarized to <paramref name="summarize"/> and marks the rows it returns as
    /// summarized at <paramref name="at"/> — after it returned, and only those. No event is recorded meanwhile, so an event
    /// held just as its window ended is either in the summary or (its row already summarized) delivered by its producer.</summary>
    Task SummarizeAsync(Func<IReadOnlyList<QuietHoursSuppressionEntity>, CancellationToken, Task<IReadOnlyCollection<string>>> summarize,
        DateTime at, CancellationToken ct = default);

    /// <summary>Removes rows summarized before <paramref name="cutoff"/>.</summary>
    Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <inheritdoc cref="IAlertSuppressionStore"/>
public sealed class AlertSuppressionStore(IServiceProvider services) : IAlertSuppressionStore
{
    /// <summary>The events kept per rule, window and target; the rest are counted.</summary>
    public const int MaxItems = 20;

    /// <summary>The longest detail line kept per event.</summary>
    internal const int MaxDetail = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One writer at a time: a row is read, extended and written back; parallel producers (the container stats fan-out)
    // would otherwise lose counts.
    private readonly SemaphoreSlim _write = new(1, 1);

    internal static string IdOf(QuietHoursMatch match, NotificationEvent evt) =>
        $"{match.RuleId}|{match.WindowKey}|{TargetOf(evt)}";

    internal static string TargetOf(NotificationEvent evt)
    {
        var server = string.IsNullOrWhiteSpace(evt.ServerName) ? evt.ServerId : evt.ServerName;
        return string.IsNullOrWhiteSpace(evt.ContainerName) || evt.ContainerName == server
            ? server ?? "Whiskers"
            : string.IsNullOrWhiteSpace(server) ? evt.ContainerName : $"{evt.ContainerName} @ {server}";
    }

    public async Task<bool> HoldAsync(QuietHoursMatch match, NotificationEvent evt, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var id = IdOf(match, evt);
            var row = await db.QuietHoursSuppressions.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is { SummarizedAt: not null }) return false;
            if (row is null)
            {
                row = new QuietHoursSuppressionEntity
                {
                    Id = id, RuleId = match.RuleId, RuleName = match.RuleName, WindowKey = match.WindowKey,
                    Target = TargetOf(evt), FirstAt = evt.Timestamp,
                };
                db.QuietHoursSuppressions.Add(row);
            }

            row.Count++;
            row.LastAt = evt.Timestamp;
            var items = Items(row);
            if (items.Count < MaxItems)
            {
                var (title, _) = NotificationFormatter.Describe(evt);
                var detail = NotificationFormatter.Detail(evt);
                if (detail.Length > MaxDetail) detail = detail[..MaxDetail] + "…";
                items.Add(new SuppressedItem(evt.Timestamp, evt.EventType, title, detail));
                row.ItemsJson = JsonSerializer.Serialize(items, Json);
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally { _write.Release(); }
    }

    public async Task SummarizeAsync(
        Func<IReadOnlyList<QuietHoursSuppressionEntity>, CancellationToken, Task<IReadOnlyCollection<string>>> summarize,
        DateTime at, CancellationToken ct = default)
    {
        // Held for the whole pass, including the summarizer's outbox write: no event may be added to a window between its
        // summary and the mark. A hold that waits past the producer's deadline meanwhile is delivered instead of held
        // (CompositeNotificationService.HeldAsync) — deliberately fail-open (review NIT-11).
        await _write.WaitAsync(ct);
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var pending = await db.QuietHoursSuppressions.AsNoTracking().Where(r => r.SummarizedAt == null)
                .OrderBy(r => r.FirstAt).ToListAsync(ct);
            if (pending.Count == 0) return;
            var done = (await summarize(pending, ct)).ToList();
            if (done.Count == 0) return;
            await db.QuietHoursSuppressions.Where(r => done.Contains(r.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.SummarizedAt, at), ct);
        }
        finally { _write.Release(); }
    }

    public async Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.QuietHoursSuppressions.Where(r => r.SummarizedAt != null && r.SummarizedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>The events kept in a row.</summary>
    public static List<SuppressedItem> Items(QuietHoursSuppressionEntity row)
    {
        try { return JsonSerializer.Deserialize<List<SuppressedItem>>(row.ItemsJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
