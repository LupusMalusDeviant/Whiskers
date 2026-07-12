using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>Single source of truth for turning a <see cref="NotificationEvent"/> into a human title,
/// severity and detail line. Shared by the in-app store and the outbound channels
/// (Telegram/Ntfy/Discord/Email/Webhook) so they stay consistent.</summary>
public static class NotificationFormatter
{
    public static (string Title, string Severity) Describe(NotificationEvent e) => e.EventType switch
    {
        "unhealthy" => ("Container unhealthy", "Error"),
        "oom_killed" => ("Container OOM-killed", "Error"),
        "stopped" => ("Container stopped", "Error"),
        "restart_loop" => ("Restart loop", "Warning"),
        "image_update" => ("Image update available", "Info"),
        "cve_finding" => ("New CVE", "Error"),
        "high_cpu" => ("High CPU load", "Error"),
        "high_memory" => ("High memory load", "Error"),
        "high_disk" => ("High disk usage", "Error"),
        "metric_anomaly" => ("Metric anomaly", "Warning"),
        "agent_action" => ("AI-Agent", "Info"),
        "agent_approval" => ("Approval required", "Warning"),
        "auto_update_failed" => ("Auto-update failed", "Error"),
        "webhook_disabled" => ("Webhook disabled", "Warning"),
        _ when e.EventType.StartsWith("log_alert", StringComparison.Ordinal) => ("Log alert / error in log", "Warning"),
        _ => (e.EventType, "Info"),
    };

    /// <summary>Detail line: the event's ImageInfo if present, else container · image · exit · restarts.</summary>
    public static string Detail(NotificationEvent e) =>
        !string.IsNullOrWhiteSpace(e.ImageInfo)
            ? e.ImageInfo!
            : string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(e.ContainerName) ? null : e.ContainerName,
                string.IsNullOrWhiteSpace(e.Image) ? null : e.Image,
                e.ExitCode is { } ec ? $"Exit {ec}" : null,
                e.RestartCount is { } rc ? $"×{rc}" : null,
            }.Where(s => s is not null));

    /// <summary>Relative, path-base-safe in-app link target for a notification (null = not navigable).</summary>
    public static string? LinkFor(NotificationEvent e)
    {
        if (e.EventType == "agent_approval") return "approvals";
        if (e.EventType == "webhook_disabled") return "webhooks";
        if (e.EventType.StartsWith("agent_action", StringComparison.Ordinal)) return "agent-history";
        if (e.EventType == "cve_finding") return "cves";
        if (e.EventType.StartsWith("log_alert", StringComparison.Ordinal)) return "logs";
        if (e.EventType is "image_update" or "auto_update_failed"
                or "unhealthy" or "oom_killed" or "stopped" or "restart_loop"
                or "high_cpu" or "high_memory" or "metric_anomaly"
            && !string.IsNullOrWhiteSpace(e.ContainerId))
            return $"container/{e.ContainerId}";
        if (e.EventType is "image_update" or "auto_update_failed") return ""; // fallback: dashboard
        return null;
    }

    /// <summary>Plain "Title — detail" for channels without rich formatting.</summary>
    public static string PlainText(NotificationEvent e)
    {
        var (title, _) = Describe(e);
        var detail = Detail(e);
        return string.IsNullOrWhiteSpace(detail) ? title : $"{title}\n{detail}";
    }
}
