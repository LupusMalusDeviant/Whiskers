namespace Whiskers.Models;

/// <summary>A rule that runs the agent autonomously when a matching event occurs.
/// The agent runs with the chosen guardrail preset as its ceiling.</summary>
public class AiTrigger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New trigger";
    public bool Enabled { get; set; } = true;

    /// <summary>NotificationEvent.EventType values this trigger reacts to
    /// (e.g. "unhealthy", "oom_killed", "stopped", "restart_loop", "image_update", "cve_finding", "log_alert").</summary>
    public List<string> EventTypes { get; set; } = new();

    /// <summary>Optional glob on the container/image name — empty = any.</summary>
    public string NameFilter { get; set; } = "";

    /// <summary>The instruction / workflow handed to the agent when this trigger fires.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Name of the guardrail preset that governs the autonomous run.</summary>
    public string GuardrailPreset { get; set; } = "Standard";

    /// <summary>The capabilities (catalog ids such as <c>container.restart</c>) the trigger may perform (PC-D7, R1-13).
    /// Empty — the default, and every trigger saved before R1-13 — means the trigger is read-only. An Admin assigns
    /// them explicitly; nothing else ever widens them.</summary>
    public List<string> WriteCapabilities { get; set; } = new();

    /// <summary>Globs on the resolved target's id or name (<c>web</c>, <c>shop-*</c>) a granted capability may act on.
    /// Empty means no target, so the trigger is read-only as well.</summary>
    public List<string> WriteTargets { get; set; } = new();

    /// <summary>The Admin who assigned the write grant (<c>user:&lt;email&gt;</c>), set by the store, never by the
    /// caller (PC-D6). Writes run on this owner's authority, re-checked for every mutation.</summary>
    public string? OwnerId { get; set; }

    /// <summary>Why the trigger was switched off because its owner lost their authority; cleared when an Admin
    /// switches it on again, which makes that Admin its owner.</summary>
    public string? SuspendedReason { get; set; }

    /// <summary>Minimum seconds between runs of this trigger (per container) — avoids storms.</summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>Whether the trigger may write at all: it has capabilities, targets and an owner.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasWriteGrant =>
        WriteCapabilities.Count > 0 && WriteTargets.Count > 0 && !string.IsNullOrWhiteSpace(OwnerId);
}

/// <summary>The known event types a trigger can react to, with an English label (the agent's task message; the page shows the localized text `AiTriggersView_Event_&lt;type&gt;`).</summary>
public static class AiTriggerEvents
{
    public static readonly IReadOnlyList<(string Type, string Label)> All = new[]
    {
        ("unhealthy",     "Container unhealthy"),
        ("oom_killed",    "Container OOM-killed"),
        ("stopped",       "Container stopped"),
        ("restart_loop",  "Restart loop (crash loop)"),
        ("image_update",  "Image update available"),
        ("cve_finding",   "New CVE found"),
        ("log_alert",     "Log alert / error in the log"),
        ("server_unreachable", "Server unreachable"),
        ("server_recovered",   "Server reachable again"),
        ("auto_update_failed", "Auto-update failed"),
        ("high_cpu",      "High CPU load (threshold)"),
        ("high_memory",   "High memory load (threshold)"),
        ("metric_anomaly", "Metric outlier (anomaly)"),
        ("certificate_expiring", "Certificate expiring soon"),
        ("certificate_expired",  "Certificate expired"),
    };

    public static string Label(string type) =>
        All.FirstOrDefault(e => e.Type == type).Label ?? type;
}
