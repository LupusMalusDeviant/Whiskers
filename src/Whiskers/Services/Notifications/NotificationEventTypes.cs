using System.Reflection;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>
/// R1-56: the one registry of notification event types. Every producer draws its <see cref="NotificationEvent.EventType"/>
/// from here — a constant, or one of the two runtime patterns <c>log_alert:&lt;severity&gt;</c> (<see cref="LogAlert"/>)
/// and <c>&lt;kind&gt;_recovered</c> (<see cref="Recovered"/>, for the kinds in <see cref="RecoverableKinds"/>) — and
/// <see cref="NotificationFormatter.Describe"/>, the notifications log and the quiet-hours rule read it.
/// <c>NotificationEventTypeArchitectureTests</c> refuses an event type written anywhere else.
///
/// <para>Every entry is classified once: <see cref="UnsuppressibleEvents"/> are never held by quiet hours, the rest are
/// <see cref="SuppressibleEvents"/>. A classification test fails for a new constant that is in neither, so a type cannot
/// land unclassified. A type the registry does not know at runtime is delivered, never held
/// (<see cref="IsSuppressible"/>).</para>
/// </summary>
public static class NotificationEventTypes
{
    // --- Containers --------------------------------------------------------------------------------------------
    public const string Unhealthy = "unhealthy";
    public const string OomKilled = "oom_killed";
    public const string Stopped = "stopped";
    public const string RestartLoop = "restart_loop";
    public const string ImageUpdate = "image_update";
    public const string AutoUpdateStart = "auto_update_start";
    public const string AutoUpdateFailed = "auto_update_failed";
    public const string CveFinding = "cve_finding";

    // --- Threshold alerts (one raise per breach and a <kind>_recovered, R1-56) -----------------------------------
    public const string HighCpu = "high_cpu";
    public const string HighMemory = "high_memory";
    public const string HighDisk = "high_disk";

    /// <summary>A series had no readings for longer than the gap tolerance: its breach state was reset (R1-56).</summary>
    public const string MetricsGap = "metrics_gap";

    /// <summary>A point-in-time outlier, not a breach: re-fires after the cool-down, has no recovery.</summary>
    public const string MetricAnomaly = "metric_anomaly";

    // --- Host rules (Plan-0004) --------------------------------------------------------------------------------
    public const string HostCpuHigh = "host_cpu_high";
    public const string HostMemoryHigh = "host_memory_high";
    public const string HostCpuUnexplained = "host_cpu_unexplained";
    public const string HostApiSlow = "host_api_slow";
    public const string HostCpuAnomaly = "host_cpu_anomaly";
    public const string HostCpuBaselineDrifted = "host_cpu_baseline_drifted";

    // --- Servers and Whiskers itself ---------------------------------------------------------------------------
    public const string ServerUnreachable = "server_unreachable";
    public const string ServerRecovered = "server_recovered";
    public const string ServerThrottled = "server_throttled";
    public const string ServerThrottlingEnded = "server_throttling_ended";
    public const string CertificateExpiring = "certificate_expiring";
    public const string CertificateExpired = "certificate_expired";
    public const string LogRotationMissing = "log_rotation_missing";
    public const string LoopsPaused = "loops_paused";
    public const string LoopsResumed = "loops_resumed";
    public const string LoopsPausedReminder = "loops_paused_reminder";
    public const string MonitoringStalled = "monitoring_stalled";
    public const string MonitoringResumed = "monitoring_resumed";
    public const string LogScanSuspended = "log_scan_suspended";
    public const string LogScanResumed = "log_scan_resumed";

    // --- Agent, automation and administration --------------------------------------------------------------------
    public const string AgentAction = "agent_action";
    public const string AgentApproval = "agent_approval";
    public const string AutomationSuspended = "automation_suspended";
    public const string AutomationRefused = "automation_refused";
    public const string OperationsInterrupted = "operations_interrupted";
    public const string WebhookDisabled = "webhook_disabled";

    /// <summary>What quiet hours held back, delivered as one message after the window (R1-56).</summary>
    public const string QuietHoursSummary = "quiet_hours_summary";

    /// <summary>A channel's own test message.</summary>
    public const string Test = "test";

    // --- The two runtime patterns --------------------------------------------------------------------------------

    /// <summary>The prefix of a log-rule alert; the rule's severity follows the colon.</summary>
    public const string LogAlertPrefix = "log_alert";

    /// <summary>The suffix of the end of a breach: <c>high_cpu</c> → <c>high_cpu_recovered</c>.</summary>
    public const string RecoveredSuffix = "_recovered";

    /// <summary><c>log_alert:&lt;severity&gt;</c> for a log rule of that severity.</summary>
    public static string LogAlert(string severity) => $"{LogAlertPrefix}:{severity}";

    /// <summary><c>&lt;kind&gt;_recovered</c> for a breach of <paramref name="kind"/> that ended.</summary>
    public static string Recovered(string kind) =>
        RecoverableKinds.Contains(kind)
            ? kind + RecoveredSuffix
            : throw new ArgumentException($"'{kind}' has no recovery event.", nameof(kind));

    /// <summary>The kinds a breach tracker raises and closes: each has a <c>&lt;kind&gt;_recovered</c>.</summary>
    public static readonly IReadOnlySet<string> RecoverableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        HighCpu, HighMemory, HighDisk, MetricsGap,
        HostCpuHigh, HostMemoryHigh, HostCpuUnexplained, HostApiSlow, HostCpuAnomaly, HostCpuBaselineDrifted,
    };

    /// <summary>Every constant above (not the two patterns).</summary>
    public static readonly IReadOnlySet<string> All = typeof(NotificationEventTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string)
                    && f.Name is not (nameof(LogAlertPrefix) or nameof(RecoveredSuffix)))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Events an admin must see whatever the target's mute or preferences say (R1-04, INV-04). Only these override
    /// the per-container mute.</summary>
    public static readonly IReadOnlySet<string> AdministrativeEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        AutomationSuspended, AutomationRefused, OperationsInterrupted,
    };

    /// <summary>
    /// Never held by quiet hours. The administrative events (INV-04); a disabled webhook (R1-09's admin notice); an approval
    /// request (it expires under R1-15 — holding it would silently kill it); a reminder that loops are paused, a suspended
    /// log scan and stalled monitoring (nothing is being checked — holding it hides the gap) and their resumption; a failed
    /// update (it can leave a container down); the summary itself and a channel test. A <c>cve_finding</c> batch holding a
    /// Critical finding is never held either (<see cref="IsSuppressible"/>). This bypasses quiet hours only: the
    /// per-container mute keeps its meaning — only <see cref="AdministrativeEvents"/> override it.
    /// </summary>
    public static readonly IReadOnlySet<string> UnsuppressibleEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        AutomationSuspended, AutomationRefused, OperationsInterrupted,
        WebhookDisabled, AgentApproval,
        LoopsPausedReminder, LogScanSuspended, LogScanResumed, MonitoringStalled, MonitoringResumed,
        AutoUpdateFailed, QuietHoursSummary, Test,
    };

    /// <summary>What quiet hours may hold (and a summary reports after the window).</summary>
    public static readonly IReadOnlySet<string> SuppressibleEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        Unhealthy, OomKilled, Stopped, RestartLoop, ImageUpdate, AutoUpdateStart, CveFinding,
        HighCpu, HighMemory, HighDisk, MetricsGap, MetricAnomaly,
        HostCpuHigh, HostMemoryHigh, HostCpuUnexplained, HostApiSlow, HostCpuAnomaly, HostCpuBaselineDrifted,
        ServerUnreachable, ServerRecovered, ServerThrottled, ServerThrottlingEnded, CertificateExpiring, CertificateExpired,
        LogRotationMissing, LoopsPaused, LoopsResumed, AgentAction,
    };

    /// <summary>Raise and end of a state: never throttled per channel. Each is announced once by its source (a breach
    /// tracker, the reachability tracker, the certificate monitor, the quiet-hours summary), so dropping one would leave a
    /// breach without its end — or an end without its breach.</summary>
    public static bool IsStateTransition(string eventType) =>
        eventType is ServerUnreachable or ServerRecovered or CertificateExpiring or CertificateExpired or QuietHoursSummary
        || RecoverableKinds.Contains(eventType)
        || IsRecovery(eventType);

    /// <summary>Whether <paramref name="eventType"/> is a <c>&lt;kind&gt;_recovered</c> of a recoverable kind.</summary>
    public static bool IsRecovery(string eventType) =>
        eventType.EndsWith(RecoveredSuffix, StringComparison.Ordinal)
        && RecoverableKinds.Contains(eventType[..^RecoveredSuffix.Length]);

    /// <summary>Whether <paramref name="eventType"/> is a <c>log_alert:&lt;severity&gt;</c>.</summary>
    public static bool IsLogAlert(string eventType) =>
        eventType.StartsWith(LogAlertPrefix + ":", StringComparison.Ordinal) && eventType.Length > LogAlertPrefix.Length + 1;

    /// <summary>A constant or one of the two patterns.</summary>
    public static bool IsKnown(string? eventType) =>
        eventType is not null && (All.Contains(eventType) || IsLogAlert(eventType) || IsRecovery(eventType));

    /// <summary>
    /// Whether quiet hours may hold <paramref name="evt"/>. Never for a type the registry does not know (delivered, never
    /// held), never for <see cref="UnsuppressibleEvents"/>, and never for a <c>cve_finding</c> batch whose highest severity
    /// is Critical — one Critical finding makes the whole batch unsuppressible.
    /// </summary>
    public static bool IsSuppressible(NotificationEvent evt)
    {
        var type = evt.EventType;
        if (!IsKnown(type) || UnsuppressibleEvents.Contains(type)) return false;
        if (type == CveFinding && evt.HighestCveSeverity is Whiskers.Models.Cve.CveSeverity.Critical) return false;
        return SuppressibleEvents.Contains(type) || IsLogAlert(type) || IsRecovery(type);
    }
}
