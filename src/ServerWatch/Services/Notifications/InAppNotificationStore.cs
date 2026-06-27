using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

/// <summary>In-memory feed of recent events for the in-app bell — no external channel needed.
/// Fed by <see cref="CompositeNotificationService"/> alongside Mattermost/Matrix.</summary>
public interface IInAppNotificationStore
{
    IReadOnlyList<InAppNotification> Recent { get; }
    int UnreadCount { get; }
    void Add(NotificationEvent evt);
    void MarkAllRead();
    void Clear();
    event Action? Changed;
    event Action<InAppNotification>? Added;
}

public sealed class InAppNotificationStore : IInAppNotificationStore
{
    private const int MaxItems = 100;
    private readonly List<InAppNotification> _items = new();
    private readonly object _lock = new();
    private int _unread;

    public event Action? Changed;
    public event Action<InAppNotification>? Added;

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
        Changed?.Invoke();
        Added?.Invoke(n);
    }

    public void MarkAllRead()
    {
        Interlocked.Exchange(ref _unread, 0);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
        Interlocked.Exchange(ref _unread, 0);
        Changed?.Invoke();
    }

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
        if (e.EventType is "image_update" or "auto_update_failed") return ""; // dashboard (path-base safe)

        // Container-scoped events → the container's detail page when we know which one.
        if (e.EventType is "unhealthy" or "oom_killed" or "stopped" or "restart_loop"
                or "high_cpu" or "high_memory" or "metric_anomaly"
            && !string.IsNullOrWhiteSpace(e.ContainerId))
            return $"container/{e.ContainerId}";

        return null;
    }
}
