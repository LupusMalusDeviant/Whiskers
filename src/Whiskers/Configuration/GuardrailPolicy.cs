namespace Whiskers.Configuration;

/// <summary>The configurable but code-enforced security policy of the agent. Persisted in its
/// own guardrails.json (admin-only). Defaults are deliberately restrictive:
/// read-only autonomous, writes need confirmation, Whiskers itself is protected.</summary>
public class GuardrailPolicy
{
    /// <summary>Highest level the agent may execute WITHOUT confirmation (read|write|admin).</summary>
    public string MaxAutonomousLevel { get; set; } = "read";

    /// <summary>Kill switch: everything above read → Deny, regardless of any other setting.</summary>
    public bool ReadOnlyMode { get; set; }

    /// <summary>Writes (and higher) require explicit UI confirmation (hybrid mode).</summary>
    public bool RequireConfirmationForWrites { get; set; } = true;

    /// <summary>These tools are always Deny — independent of the trigger's role/key.</summary>
    public List<string> ToolDenyList { get; set; } = new();

    /// <summary>If not empty: ONLY these tools are allowed, everything else Deny.</summary>
    public List<string> ToolAllowList { get; set; } = new();

    /// <summary>Tool gating mode for the clickable grid: "deny" (all allowed except ToolDenyList) or
    /// "allow" (only ToolAllowList allowed — default-deny). In "allow" mode the whitelist is enforced
    /// even when empty (= nothing allowed).</summary>
    public string ToolMode { get; set; } = "deny";

    /// <summary>Glob patterns for resources (container/server/DB) the agent must never touch.</summary>
    public List<string> ProtectedResources { get; set; } = new();

    /// <summary>Regex patterns against tool arguments (e.g. destructive shell/SQL) → Deny on match.</summary>
    public List<string> ForbiddenArgPatterns { get; set; } = new();

    /// <summary>Maximum number of executed actions per session — code-enforced.</summary>
    public int MaxActionsPerSession { get; set; } = 20;

    /// <summary>Restrictive default: read-only autonomous, writes via confirmation, Whiskers protected,
    /// obviously destructive shell/SQL patterns blocked.</summary>
    public static GuardrailPolicy SafeDefault() => new()
    {
        MaxAutonomousLevel = "read",
        ReadOnlyMode = false,
        RequireConfirmationForWrites = true,
        ProtectedResources = { "serverwatch*" },
        ForbiddenArgPatterns =
        {
            @"rm\s+-rf\s+/",
            @"\bDROP\s+(DATABASE|TABLE)\b",
            @"\bmkfs\b",
            @":\(\)\s*\{",            // fork bomb
        },
        MaxActionsPerSession = 20,
    };
}
