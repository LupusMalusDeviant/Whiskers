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
        "server_unreachable" => ("Server unreachable", "Error"),
        "server_recovered" => ("Server reachable again", "Info"),
        "certificate_expiring" => ("Certificate expires soon", "Warning"),
        "certificate_expired" => ("Certificate expired", "Error"),
        // Whiskers throttling ITSELF. Never silent: a self-imposed pause that nobody is told about
        // turns "quiet" into "blind", and hides the next incident behind the fix for the last one.
        "host_cpu_anomaly" => ("Server CPU is far from this host's normal", "Warning"),
        "host_cpu_anomaly_recovered" => ("Server CPU is back within its usual range", "Info"),
        "host_cpu_baseline_drifted" => ("The learned normal has drifted past the threshold", "Warning"),
        "host_cpu_baseline_drifted_recovered" => ("The learned normal is back below the threshold", "Info"),
        "host_api_slow" => ("Docker is answering much slower than usual", "Warning"),
        "host_api_slow_recovered" => ("Docker is answering at its usual speed again", "Info"),
        "host_cpu_high" => ("Server CPU high for a sustained period", "Warning"),
        "host_cpu_high_recovered" => ("Server CPU back to normal", "Info"),
        "host_memory_high_recovered" => ("Server memory back to normal", "Info"),
        "host_cpu_unexplained_recovered" => ("Host load is explained by its containers again", "Info"),
        "host_memory_high" => ("Server memory high for a sustained period", "Warning"),
        "host_cpu_unexplained" => ("Host load no container accounts for", "Warning"),
        "log_rotation_missing" => ("Container log growing without a rotation limit", "Warning"),
        "loops_paused" => ("Background checks paused for this server", "Warning"),
        "loops_resumed" => ("Background checks running again", "Info"),
        "monitoring_stalled" => ("Nothing is being checked here", "Error"),
        "monitoring_resumed" => ("Checks are running again", "Info"),
        "log_scan_suspended" => ("Log scan suspended for this container", "Warning"),
        "log_scan_resumed" => ("Log scan resumed for this container", "Info"),
        "server_throttled" => ("Whiskers paused its own calls to this server", "Warning"),
        "server_throttling_ended" => ("Whiskers resumed calls to this server", "Info"),
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
                string.IsNullOrWhiteSpace(e.ServerName) ? null : $"@ {e.ServerName}",
                string.IsNullOrWhiteSpace(e.Image) ? null : e.Image,
                e.ExitCode is { } ec ? $"Exit {ec}" : null,
                e.RestartCount is { } rc ? $"×{rc}" : null,
            }.Where(s => s is not null));

    /// <summary>Relative, path-base-safe in-app link target for a notification (null = not navigable).</summary>
    public static string? LinkFor(NotificationEvent e)
    {
        if (e.EventType == "agent_approval")
            return string.IsNullOrWhiteSpace(e.ApprovalId) ? "approvals" : Approvals.ApprovalDisplay.LinkOf(e.ApprovalId);
        if (e.EventType == "webhook_disabled") return "webhooks";
        if (e.EventType.StartsWith("agent_action", StringComparison.Ordinal)) return "agent-history";
        if (e.EventType == "cve_finding") return "cves";
        if (e.EventType is "server_unreachable" or "server_recovered"
                or "server_throttled" or "server_throttling_ended") return "servers";
        if (e.EventType.StartsWith("host_")) return "servers";
        if (e.EventType == "log_rotation_missing") return "servers";
        if (e.EventType is "certificate_expiring" or "certificate_expired") return "servers";
        if (e.EventType is "loops_paused" or "loops_resumed") return "servers";
        if (e.EventType is "monitoring_stalled" or "monitoring_resumed") return "servers";
        if (e.EventType is "log_scan_suspended" or "log_scan_resumed") return "logs";
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
