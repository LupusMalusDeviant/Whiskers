using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>Single source of truth for turning a <see cref="NotificationEvent"/> into a human title,
/// severity and detail line. Shared by the in-app store and the outbound channels
/// (Telegram/Ntfy/Discord/Email/Webhook) so they stay consistent.</summary>
public static class NotificationFormatter
{
    /// <summary>Title and severity of an event type, read from <see cref="NotificationEventTypes"/> (R1-56). A type the
    /// registry does not know keeps its raw id as the title.</summary>
    public static (string Title, string Severity) Describe(NotificationEvent e) => e.EventType switch
    {
        NotificationEventTypes.Unhealthy => ("Container unhealthy", "Error"),
        NotificationEventTypes.OomKilled => ("Container OOM-killed", "Error"),
        NotificationEventTypes.Stopped => ("Container stopped", "Error"),
        NotificationEventTypes.RestartLoop => ("Restart loop", "Warning"),
        NotificationEventTypes.ImageUpdate => ("Image update available", "Info"),
        NotificationEventTypes.AutoUpdateStart => ("Auto-update starting", "Info"),
        NotificationEventTypes.CveFinding => ("New CVE", "Error"),
        NotificationEventTypes.HighCpu => ("High CPU load", "Error"),
        NotificationEventTypes.HighMemory => ("High memory load", "Error"),
        NotificationEventTypes.HighDisk => ("High disk usage", "Error"),
        // R1-56: the end of a breach, and a series that stopped delivering readings.
        "high_cpu_recovered" => ("CPU load back to normal", "Info"),
        "high_memory_recovered" => ("Memory load back to normal", "Info"),
        "high_disk_recovered" => ("Disk usage back to normal", "Info"),
        NotificationEventTypes.MetricsGap => ("No readings: alerts cannot fire for this", "Warning"),
        "metrics_gap_recovered" => ("Readings are back", "Info"),
        NotificationEventTypes.MetricAnomaly => ("Metric anomaly", "Warning"),
        NotificationEventTypes.AgentAction => ("AI-Agent", "Info"),
        NotificationEventTypes.AgentApproval => ("Approval required", "Warning"),
        NotificationEventTypes.AutoUpdateFailed => ("Auto-update failed", "Error"),
        NotificationEventTypes.WebhookDisabled => ("Webhook disabled", "Warning"),
        NotificationEventTypes.AutomationSuspended => ("Automation suspended", "Warning"),
        NotificationEventTypes.AutomationRefused => ("Automation refused", "Warning"),
        NotificationEventTypes.ServerUnreachable => ("Server unreachable", "Error"),
        NotificationEventTypes.ServerRecovered => ("Server reachable again", "Info"),
        NotificationEventTypes.CertificateExpiring => ("Certificate expires soon", "Warning"),
        NotificationEventTypes.CertificateExpired => ("Certificate expired", "Error"),
        // Whiskers throttling ITSELF. Never silent: a self-imposed pause that nobody is told about
        // turns "quiet" into "blind", and hides the next incident behind the fix for the last one.
        NotificationEventTypes.HostCpuAnomaly => ("Server CPU is far from this host's normal", "Warning"),
        "host_cpu_anomaly_recovered" => ("Server CPU is back within its usual range", "Info"),
        NotificationEventTypes.HostCpuBaselineDrifted => ("The learned normal has drifted past the threshold", "Warning"),
        "host_cpu_baseline_drifted_recovered" => ("The learned normal is back below the threshold", "Info"),
        NotificationEventTypes.HostApiSlow => ("Docker is answering much slower than usual", "Warning"),
        "host_api_slow_recovered" => ("Docker is answering at its usual speed again", "Info"),
        NotificationEventTypes.HostCpuHigh => ("Server CPU high for a sustained period", "Warning"),
        "host_cpu_high_recovered" => ("Server CPU back to normal", "Info"),
        "host_memory_high_recovered" => ("Server memory back to normal", "Info"),
        "host_cpu_unexplained_recovered" => ("Host load is explained by its containers again", "Info"),
        NotificationEventTypes.HostMemoryHigh => ("Server memory high for a sustained period", "Warning"),
        NotificationEventTypes.HostCpuUnexplained => ("Host load no container accounts for", "Warning"),
        NotificationEventTypes.LogRotationMissing => ("Container log growing without a rotation limit", "Warning"),
        NotificationEventTypes.LoopsPaused => ("Background checks paused for this server", "Warning"),
        NotificationEventTypes.LoopsResumed => ("Background checks running again", "Info"),
        NotificationEventTypes.LoopsPausedReminder => ("Background checks are still paused", "Warning"),
        NotificationEventTypes.MonitoringStalled => ("Nothing is being checked here", "Error"),
        NotificationEventTypes.MonitoringResumed => ("Checks are running again", "Info"),
        NotificationEventTypes.LogScanSuspended => ("Log scan suspended for this container", "Warning"),
        NotificationEventTypes.LogScanResumed => ("Log scan resumed for this container", "Info"),
        NotificationEventTypes.ServerThrottled => ("Whiskers paused its own calls to this server", "Warning"),
        NotificationEventTypes.ServerThrottlingEnded => ("Whiskers resumed calls to this server", "Info"),
        NotificationEventTypes.OperationsInterrupted => ("Work was cut off by a stop: check its targets", "Warning"),
        NotificationEventTypes.QuietHoursSummary => ("Held back during quiet hours", "Info"),
        _ when NotificationEventTypes.IsLogAlert(e.EventType) => ("Log alert / error in log", "Warning"),
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
        // R1-56: a container's gap or breach end opens the container; a host's (or its disk's) the servers.
        if (e.EventType is NotificationEventTypes.MetricsGap or "metrics_gap_recovered"
                or "high_cpu_recovered" or "high_memory_recovered")
            return string.IsNullOrWhiteSpace(e.ContainerId) ? "servers" : $"container/{e.ContainerId}";
        if (e.EventType is NotificationEventTypes.HighDisk or "high_disk_recovered") return "servers";
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
