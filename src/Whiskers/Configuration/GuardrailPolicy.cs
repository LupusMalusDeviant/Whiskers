using System.Text.Json.Serialization;

namespace Whiskers.Configuration;

/// <summary>Contract 0009: the exemptions of the read-only kill switch. E1 switching it off, E2 the first-run setup and
/// E3 the host CLIs are fixed; the three below are allowed by default and can be blocked beforehand
/// (<see cref="GuardrailPolicy.KillSwitchBlockedExemptions"/>). Nobody can add one.</summary>
public static class KillSwitchExemptions
{
    /// <summary>E4: a user's own password change (<c>account.password_change</c>, contract 0002 D4).</summary>
    public const string PasswordChange = "password_change";

    /// <summary>E5: setting a new password with a recovery code (contract 0002 D8).</summary>
    public const string RecoveryCode = "recovery_code";

    /// <summary>E6: the federated identity binding written at a verified sign-in (contract 0003).</summary>
    public const string FederatedBindingAtSignIn = "federated_binding_at_sign_in";

    /// <summary>The ids <see cref="GuardrailPolicy.KillSwitchBlockedExemptions"/> may hold, in the order the UI shows them.</summary>
    public static IReadOnlyList<string> Blockable { get; } = [PasswordChange, RecoveryCode, FederatedBindingAtSignIn];

    /// <summary>The rule a refusal of a blocked exemption names, as the kill switch's own.</summary>
    public const string RuleId = "read-only-mode";
}

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

    private List<string>? _killSwitchBlockedExemptions;

    /// <summary>Contract 0009 (D3): the exemptions of the kill switch an Admin blocked beforehand, out of
    /// <see cref="KillSwitchExemptions.Blockable"/>. While the kill switch is on, a blocked exemption is refused like any
    /// other mutation. Optional in <c>guardrails.json</c>, and left out of the stored and hashed policy while empty (an
    /// empty list reads as <c>null</c>), so existing policies keep their version. An unknown id makes the configuration
    /// invalid (the store fails closed, INV-25).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? KillSwitchBlockedExemptions
    {
        get => _killSwitchBlockedExemptions is { Count: > 0 } ? _killSwitchBlockedExemptions : null;
        set => _killSwitchBlockedExemptions = value;
    }

    /// <summary>Contract 0009: whether the kill switch is on and <paramref name="exemption"/> is blocked under it.</summary>
    public bool KillSwitchBlocks(string exemption) =>
        ReadOnlyMode && KillSwitchBlockedExemptions?.Contains(exemption, StringComparer.Ordinal) == true;

    /// <summary>The label prefixes Whiskers built in for exposure changes (contract 0006): a tool on the host turns a label
    /// with one of them into a route (Tailscale Funnel or Serve through DockTail, Traefik, caddy-docker-proxy).</summary>
    public static IReadOnlyList<string> DefaultExposureLabelPrefixes { get; } =
        ["docktail.funnel.", "docktail.service.", "traefik.http.routers.", "traefik.tcp.routers.", "caddy"];

    /// <summary>The exposure classes the agent and API keys need a confirmation for by default (contract 0006, D1).</summary>
    public static IReadOnlyList<string> DefaultExposureConfirmClasses { get; } = ["public", "host"];

    /// <summary>Contract 0006: label key prefixes (case-insensitive) that make a compose label an exposure change. Optional
    /// in <c>guardrails.json</c>; <c>null</c> means the default.</summary>
    public List<string>? ExposureLabelPrefixes { get; set; } = [.. DefaultExposureLabelPrefixes];

    /// <summary>Contract 0006: the exposure classes (<c>public</c>, <c>host</c>, <c>published</c>) for which the agent and
    /// API keys need a confirmation. Optional in <c>guardrails.json</c>; <c>null</c> means the default, an empty list turns
    /// the confirmation off (an explicit operator choice).</summary>
    public List<string>? ExposureConfirmClasses { get; set; } = [.. DefaultExposureConfirmClasses];

    /// <summary>The prefixes in force: <see cref="ExposureLabelPrefixes"/>, or the default when it is <c>null</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveExposureLabelPrefixes =>
        ExposureLabelPrefixes is { } prefixes
            ? prefixes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            : DefaultExposureLabelPrefixes;

    /// <summary>The classes in force: <see cref="ExposureConfirmClasses"/>, or the default when it is <c>null</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveExposureConfirmClasses =>
        ExposureConfirmClasses is { } classes
            ? classes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToLowerInvariant()).ToList()
            : DefaultExposureConfirmClasses;

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
        // null (the default) stays null.
        ExposureLabelPrefixes = ExposureLabelPrefixes is null ? null : [.. ExposureLabelPrefixes],
        ExposureConfirmClasses = ExposureConfirmClasses is null ? null : [.. ExposureConfirmClasses],
        KillSwitchBlockedExemptions = KillSwitchBlockedExemptions is null ? null : [.. KillSwitchBlockedExemptions],
    };

    /// <summary>What applies while no valid policy could be loaded (INV-25): every mutation refused (kill switch),
    /// no tool allowed for agents and MCP clients, every resource protected, and every blockable kill-switch exemption
    /// blocked (contract 0009, D4: the host CLIs stay usable; the way back is repairing guardrails.json, A1.2).</summary>
    public static GuardrailPolicy FailClosed() => new()
    {
        MaxAutonomousLevel = "read",
        ReadOnlyMode = true,
        RequireConfirmationForWrites = true,
        ToolMode = "allow",
        ProtectedResources = { "*" },
        MaxActionsPerSession = 0,
        RequireSecondApprover = true,
        KillSwitchBlockedExemptions = [.. KillSwitchExemptions.Blockable],
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
