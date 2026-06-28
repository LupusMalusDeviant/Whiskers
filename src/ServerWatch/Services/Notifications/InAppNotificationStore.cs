using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Notifications;

/// <summary>Feed of notification events for the in-app bell and the /notifications page. Keeps a
/// small in-memory cache for the bell's live updates and write-through-persists every event to
/// SQLite so the history survives restarts. Fed by <see cref="CompositeNotificationService"/>
/// alongside Mattermost/Matrix.</summary>
public interface IInAppNotificationStore
{
    IReadOnlyList<InAppNotification> Recent { get; }
    int UnreadCount { get; }
    void Add(NotificationEvent evt);
    void MarkAllRead();
    void Clear();
    event Action? Changed;
    event Action<InAppNotification>? Added;

    /// <summary>Page of persisted notifications (newest first), optionally filtered. For the /notifications page.</summary>
    Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take, CancellationToken ct = default);
    Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default);
}

public sealed class InAppNotificationStore : IInAppNotificationStore
{
    private const int MaxItems = 100;       // in-memory cache for the bell dropdown
    private const int MaxPersisted = 2000;  // hard cap on the persisted history
    private readonly List<InAppNotification> _items = new();
    private readonly object _lock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InAppNotificationStore> _logger;
    private int _unread;

    public event Action? Changed;
    public event Action<InAppNotification>? Added;

    public InAppNotificationStore(IServiceScopeFactory scopeFactory, ILogger<InAppNotificationStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        Hydrate();
    }

    public IReadOnlyList<InAppNotification> Recent
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int UnreadCount => Volatile.Read(ref _unread);

    public void Add(NotificationEvent evt)
    {
        var n = Format(evt);
        lock (_lock)
        {
            _items.Insert(0, n);
            if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            Interlocked.Increment(ref _unread);
        }
        Persist(n);
        Changed?.Invoke();
        Added?.Invoke(n);
    }

    public void MarkAllRead()
    {
        Interlocked.Exchange(ref _unread, 0);
        RunDb(db => db.Database.ExecuteSqlRaw("UPDATE \"Notifications\" SET \"Read\" = 1 WHERE \"Read\" = 0"));
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
        Interlocked.Exchange(ref _unread, 0);
        RunDb(db => db.Database.ExecuteSqlRaw("DELETE FROM \"Notifications\""));
        Changed?.Invoke();
    }

    public async Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var rows = await Filter(db.Notifications.AsNoTracking(), severity, eventType)
                .OrderByDescending(e => e.Timestamp).Skip(skip).Take(take).ToListAsync(ct);
            return rows.Select(ToModel).ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification query failed"); return new(); }
    }

    public async Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            return await Filter(db.Notifications.AsNoTracking(), severity, eventType).CountAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification count failed"); return 0; }
    }

    private static IQueryable<NotificationEntity> Filter(IQueryable<NotificationEntity> q, string? severity, string? eventType)
    {
        if (!string.IsNullOrWhiteSpace(severity)) q = q.Where(e => e.Severity == severity);
        if (!string.IsNullOrWhiteSpace(eventType)) q = q.Where(e => e.EventType == eventType);
        return q;
    }

    /// <summary>Load the recent feed + unread count from the DB so the bell survives a restart.</summary>
    private void Hydrate()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var rows = db.Notifications.AsNoTracking().OrderByDescending(e => e.Timestamp).Take(MaxItems).ToList();
            lock (_lock)
            {
                _items.Clear();
                _items.AddRange(rows.Select(ToModel));
            }
            _unread = db.Notifications.Count(e => !e.Read);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification hydrate skipped (DB not ready?)");
        }
    }

    private void Persist(InAppNotification n)
    {
        RunDb(db =>
        {
            db.Notifications.Add(new NotificationEntity
            {
                NotificationId = n.Id, Timestamp = n.Timestamp, EventType = n.EventType,
                Title = n.Title, Detail = n.Detail, Severity = n.Severity, Link = n.Link, Read = false,
            });
            db.SaveChanges();
            // Trim the persisted history to the hard cap (keep the newest MaxPersisted rows).
            db.Database.ExecuteSqlRaw(
                "DELETE FROM \"Notifications\" WHERE \"Id\" NOT IN (SELECT \"Id\" FROM \"Notifications\" ORDER BY \"Id\" DESC LIMIT {0})",
                MaxPersisted);
        });
    }

    private void RunDb(Action<MetricsDbContext> action)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            action(db);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification DB op failed"); }
    }

    private static InAppNotification ToModel(NotificationEntity e) =>
        new(e.NotificationId, e.Timestamp, e.EventType, e.Title, e.Detail, e.Severity, e.Link);

    private static InAppNotification Format(NotificationEvent e)
    {
        var (title, severity) = e.EventType switch
        {
            "unhealthy" => ("Container unhealthy", "Error"),
            "oom_killed" => ("Container OOM-gekillt", "Error"),
            "stopped" => ("Container gestoppt", "Error"),
            "restart_loop" => ("Restart-Loop", "Warning"),
            "image_update" => ("Image-Update verfügbar", "Info"),
            "cve_finding" => ("Neue CVE", "Error"),
            "high_cpu" => ("Hohe CPU-Last", "Error"),
            "high_memory" => ("Hohe RAM-Last", "Error"),
            "metric_anomaly" => ("Metrik-Ausreißer", "Warning"),
            "agent_action" => ("AI-Agent", "Info"),
            "agent_approval" => ("Freigabe erforderlich", "Warning"),
            "auto_update_failed" => ("Auto-Update fehlgeschlagen", "Error"),
            _ when e.EventType.StartsWith("log_alert", StringComparison.Ordinal) => ("Log-Alert / Fehler im Log", "Warning"),
            _ => (e.EventType, "Info"),
        };

        var detail = !string.IsNullOrWhiteSpace(e.ImageInfo)
            ? e.ImageInfo!
            : string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(e.ContainerName) ? null : e.ContainerName,
                string.IsNullOrWhiteSpace(e.Image) ? null : e.Image,
                e.ExitCode is { } ec ? $"Exit {ec}" : null,
                e.RestartCount is { } rc ? $"×{rc}" : null,
            }.Where(s => s is not null));

        return new InAppNotification(
            Guid.NewGuid().ToString("N")[..8], e.Timestamp, e.EventType, title, detail, severity, LinkFor(e));
    }

    /// <summary>Maps an event to the in-app page it should open when the notification is clicked.
    /// Null = not navigable.</summary>
    private static string? LinkFor(NotificationEvent e)
    {
        if (e.EventType == "agent_approval") return "approvals";
        if (e.EventType.StartsWith("agent_action", StringComparison.Ordinal)) return "agent-history";
        if (e.EventType == "cve_finding") return "cves";
        if (e.EventType.StartsWith("log_alert", StringComparison.Ordinal)) return "logs";
        // Image-update + container-scoped events → the specific container's detail page when we know it.
        if (e.EventType is "image_update" or "auto_update_failed"
                or "unhealthy" or "oom_killed" or "stopped" or "restart_loop"
                or "high_cpu" or "high_memory" or "metric_anomaly"
            && !string.IsNullOrWhiteSpace(e.ContainerId))
            return $"container/{e.ContainerId}";

        if (e.EventType is "image_update" or "auto_update_failed") return ""; // fallback: dashboard

        return null;
    }
}
