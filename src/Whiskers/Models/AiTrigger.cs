namespace Whiskers.Models;

/// <summary>A rule that runs the agent autonomously when a matching event occurs.
/// The agent runs with the chosen guardrail preset as its ceiling.</summary>
public class AiTrigger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neuer Trigger";
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

/// <summary>The known event types a trigger can react to, with a German label for the UI.</summary>
public static class AiTriggerEvents
{
    public static readonly IReadOnlyList<(string Type, string Label)> All = new[]
    {
        ("unhealthy",     "Container unhealthy"),
        ("oom_killed",    "Container OOM-gekillt"),
        ("stopped",       "Container gestoppt"),
        ("restart_loop",  "Restart-Loop (Crash-Loop)"),
        ("image_update",  "Image-Update verfügbar"),
        ("cve_finding",   "Neue CVE gefunden"),
        ("log_alert",     "Log-Alert / Fehler im Log"),
        ("server_unreachable", "Server nicht erreichbar"),
        ("server_recovered",   "Server wieder erreichbar"),
        ("auto_update_failed", "Auto-Update fehlgeschlagen"),
        ("high_cpu",      "Hohe CPU-Last (Schwellwert)"),
        ("high_memory",   "Hohe RAM-Last (Schwellwert)"),
        ("metric_anomaly", "Metrik-Ausreißer (Anomalie)"),
        ("certificate_expiring", "Zertifikat läuft bald ab"),
        ("certificate_expired",  "Zertifikat abgelaufen"),
    };

    public static string Label(string type) =>
        All.FirstOrDefault(e => e.Type == type).Label ?? type;
}
