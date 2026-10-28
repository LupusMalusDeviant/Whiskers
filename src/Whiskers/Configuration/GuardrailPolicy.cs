using System.Text.Json.Serialization;

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

    /// <summary>PC-D4 (R1-15): nobody approves or rejects their own request, not even in the interactive agent chat.
    /// Off by default, so a single-admin install can work; a non-interactive request (direct MCP, AI trigger,
    /// scheduled task) is never decided by its requester either way. Optional in <c>guardrails.json</c>, and left out
    /// of the stored and hashed policy while off, so existing policies keep their version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequireSecondApprover { get; set; }

    /// <summary>A deep copy: callers that edit it never change a live policy.</summary>
    public GuardrailPolicy Clone() => new()
    {
        MaxAutonomousLevel = MaxAutonomousLevel,
        ReadOnlyMode = ReadOnlyMode,
        RequireConfirmationForWrites = RequireConfirmationForWrites,
        // A missing list stays missing, so validation still refuses it.
        ToolDenyList = ToolDenyList is null ? null! : [.. ToolDenyList],
        ToolAllowList = ToolAllowList is null ? null! : [.. ToolAllowList],
        ToolMode = ToolMode,
        ProtectedResources = ProtectedResources is null ? null! : [.. ProtectedResources],
        ForbiddenArgPatterns = ForbiddenArgPatterns is null ? null! : [.. ForbiddenArgPatterns],
        MaxActionsPerSession = MaxActionsPerSession,
        RequireSecondApprover = RequireSecondApprover,
    };

    /// <summary>What applies while no valid policy could be loaded (INV-25): every mutation refused (kill switch),
    /// no tool allowed for agents and MCP clients, every resource protected.</summary>
    public static GuardrailPolicy FailClosed() => new()
    {
        MaxAutonomousLevel = "read",
        ReadOnlyMode = true,
        RequireConfirmationForWrites = true,
        ToolMode = "allow",
        ProtectedResources = { "*" },
        MaxActionsPerSession = 0,
        RequireSecondApprover = true,
    };

    /// <summary>Restrictive default: read-only autonomous, writes via confirmation, Whiskers protected,
    /// obviously destructive shell/SQL patterns blocked.</summary>
    public static GuardrailPolicy SafeDefault() => new()
    {
        MaxAutonomousLevel = "read",
        ReadOnlyMode = false,
        RequireConfirmationForWrites = true,
        ProtectedResources = { "whiskers*", "serverwatch*" },   // current + pre-rebrand container name
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
