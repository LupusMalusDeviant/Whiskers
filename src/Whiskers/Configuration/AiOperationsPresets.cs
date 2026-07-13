using Whiskers.Models;

namespace Whiskers.Configuration;

/// <summary>WP-06: the three starter guardrail presets the "Secure AI Operations" onboarding offers.
/// Each maps to real <see cref="GuardrailPolicy"/> fields and references only real tool names from the
/// catalog (<see cref="McpPermissionLevels.DefaultToolLevels"/>) — the operator activates one and then
/// refines it on the Guardrails page. Kept as a policy source-of-truth (separate from the UI, which only
/// localizes the display names/descriptions) so the mapping can be unit-tested. Building a preset never
/// weakens anything: activation writes through the normal admin-only <c>SaveConfigAsync</c>.</summary>
public static class AiOperationsPresets
{
    public const string ObserveOnly = "Observe only";
    public const string SafeOperations = "Safe operations";
    public const string ApprovalRequired = "Approval required";

    /// <summary>Tools a starter preset always denies: everything at <b>admin</b> level (from the real
    /// catalog) plus a curated set of destructive write-level tools. No invented names — every entry is
    /// filtered against the catalog.</summary>
    public static IReadOnlyList<string> HighRiskTools()
    {
        var admin = McpPermissionLevels.DefaultToolLevels
            .Where(kv => McpPermissionLevels.Normalize(kv.Value) == McpPermissionLevels.Admin)
            .Select(kv => kv.Key);
        var destructive = new[]
            {
                "execute_query", "cloud_hard_reset", "cloud_shutdown",
                "hetzner_delete_snapshot", "hetzner_disable_backups", "hetzner_change_server_type",
            }
            .Where(McpPermissionLevels.DefaultToolLevels.ContainsKey);
        return admin.Concat(destructive).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads run; everything above read is denied by the kill switch.</summary>
    public static GuardrailPolicy ObserveOnlyPolicy()
    {
        var p = GuardrailPolicy.SafeDefault();
        p.ReadOnlyMode = true;
        return p;
    }

    /// <summary>Reads run autonomously; writes require confirmation; admin + destructive tools are denied.</summary>
    public static GuardrailPolicy SafeOperationsPolicy()
    {
        var p = GuardrailPolicy.SafeDefault();
        p.MaxAutonomousLevel = "read";
        p.RequireConfirmationForWrites = true;
        p.ToolDenyList = HighRiskTools().ToList();
        return p;
    }

    /// <summary>The tightest starter: like Safe operations, but with a small per-session action budget so
    /// every write is not just confirmed but also rate-limited — pair it with reviewing each approval.</summary>
    public static GuardrailPolicy ApprovalRequiredPolicy()
    {
        var p = SafeOperationsPolicy();
        p.MaxActionsPerSession = 8;
        return p;
    }

    public static IReadOnlyList<GuardrailPreset> Starters() => new List<GuardrailPreset>
    {
        new() { Name = ObserveOnly, Policy = ObserveOnlyPolicy() },
        new() { Name = SafeOperations, Policy = SafeOperationsPolicy() },
        new() { Name = ApprovalRequired, Policy = ApprovalRequiredPolicy() },
    };

    public static GuardrailPolicy PolicyFor(string presetName) => presetName switch
    {
        ObserveOnly => ObserveOnlyPolicy(),
        SafeOperations => SafeOperationsPolicy(),
        ApprovalRequired => ApprovalRequiredPolicy(),
        _ => GuardrailPolicy.SafeDefault(),
    };
}
