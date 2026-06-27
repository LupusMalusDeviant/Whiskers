namespace ServerWatch.Models;

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

    /// <summary>Minimum seconds between runs of this trigger (per container) — avoids storms.</summary>
    public int CooldownSeconds { get; set; } = 300;
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
        ("auto_update_failed", "Auto-Update fehlgeschlagen"),
        ("high_cpu",      "Hohe CPU-Last (Schwellwert)"),
        ("high_memory",   "Hohe RAM-Last (Schwellwert)"),
        ("metric_anomaly", "Metrik-Ausreißer (Anomalie)"),
    };

    public static string Label(string type) =>
        All.FirstOrDefault(e => e.Type == type).Label ?? type;
}
