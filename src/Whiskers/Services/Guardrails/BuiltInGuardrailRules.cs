using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Models;

namespace Whiskers.Services.Guardrails;

/// <summary>Shared helpers for the built-in rules. Rules are STATELESS and read the
/// policy from request.Context.Policy — so they are unit-testable without setup and do not need
/// to be rebuilt on policy changes.</summary>
internal static class GuardrailRuleHelpers
{
    public static int Rank(string level) => McpPermissionLevels.GetRank(level);

    /// <summary>All argument values as strings (strings directly, everything else as raw JSON).</summary>
    public static IEnumerable<string> ArgumentValues(GuardrailRequest req)
    {
        foreach (var kv in req.Arguments)
        {
            var v = kv.Value;
            yield return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.GetRawText();
        }
    }

    /// <summary>How long one policy pattern may take against one value. A pattern that takes longer counts as a
    /// match (INV-25: an evaluation error never allows).</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Glob (* and ?) → anchored, case-insensitive regex. A timeout counts as a match (fail closed).</summary>
    public static bool GlobMatch(string glob, string value)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try { return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, PatternTimeout); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>The first pattern in <paramref name="patterns"/> that is not a valid regex, or <c>null</c>.</summary>
    public static string? FirstInvalidRegex(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase, PatternTimeout); }
            catch (ArgumentException) { return pattern; }
        }
        return null;
    }
}

/// <summary>The agent may NEVER exceed the rights of its trigger. Deny when the tool exceeds the
/// principal's permission level or lies outside its AllowedTools.</summary>
public sealed class PrincipalCeilingRule : IGuardrailRule
{
    public string Id => "principal-ceiling";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var p = request.Context.Principal;
        if (p.AllowedTools != null && !p.AllowedTools.Contains(request.ToolName))
        {
            reason = $"'{request.ToolName}' is not in the trigger principal's tool whitelist ({p.DisplayName}).";
            return GuardrailVerdict.Deny;
        }
        if (GuardrailRuleHelpers.Rank(request.RequiredLevel) > GuardrailRuleHelpers.Rank(p.PermissionLevel))
        {
            reason = $"'{request.ToolName}' requires '{request.RequiredLevel}', the trigger principal only has '{p.PermissionLevel}'.";
            return GuardrailVerdict.Deny;
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Kill switch: in ReadOnlyMode everything above read is an immediate Deny.</summary>
public sealed class ReadOnlyModeRule : IGuardrailRule
{
    public string Id => "read-only-mode";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        if (request.Context.Policy.ReadOnlyMode && request.RequiredLevel != McpPermissionLevels.Read)
        {
            reason = $"Read-only mode active: '{request.ToolName}' ({request.RequiredLevel}) is blocked.";
            return GuardrailVerdict.Deny;
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Explicit tool deny list — always wins.</summary>
public sealed class ToolDenyListRule : IGuardrailRule
{
    public string Id => "tool-deny-list";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        if (request.Context.Policy.ToolDenyList.Contains(request.ToolName))
        {
            reason = $"'{request.ToolName}' is on the guardrail deny list.";
            return GuardrailVerdict.Deny;
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Whitelist rule. Active when an allow list is set OR the policy is in "allow" mode
/// (default-deny). In allow mode the whitelist is enforced even when empty (= nothing allowed).</summary>
public sealed class ToolAllowListRule : IGuardrailRule
{
    public string Id => "tool-allow-list";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var policy = request.Context.Policy;
        var allow = policy.ToolAllowList;
        var whitelistActive = string.Equals(policy.ToolMode, "allow", StringComparison.OrdinalIgnoreCase)
                              || allow.Count > 0;
        if (whitelistActive && !allow.Contains(request.ToolName))
        {
            reason = $"'{request.ToolName}' is not on the guardrail allow list.";
            return GuardrailVerdict.Deny;
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Protected resources (glob): if a matching argument appears → Deny.</summary>
public sealed class ProtectedResourceRule : IGuardrailRule
{
    public string Id => "protected-resource";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var globs = request.Context.Policy.ProtectedResources;
        if (globs.Count > 0)
        {
            foreach (var value in GuardrailRuleHelpers.ArgumentValues(request))
            {
                foreach (var glob in globs)
                {
                    if (GuardrailRuleHelpers.GlobMatch(glob, value))
                    {
                        reason = $"'{value}' is a protected resource (pattern '{glob}').";
                        return GuardrailVerdict.Deny;
                    }
                }
            }
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Forbidden argument patterns (regex) against destructive shell/SQL — match → Deny.</summary>
public sealed class ForbiddenArgumentRule : IGuardrailRule
{
    public string Id => "forbidden-argument";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var patterns = request.Context.Policy.ForbiddenArgPatterns;
        if (patterns.Count > 0)
        {
            // INV-25: a pattern the engine cannot evaluate refuses every call, with or without arguments — a broken
            // policy must never read as "nothing forbidden".
            if (GuardrailRuleHelpers.FirstInvalidRegex(patterns) is { } invalid)
            {
                reason = $"The guardrail policy holds an invalid forbidden-argument pattern ('{invalid}'); refused until it is fixed.";
                return GuardrailVerdict.Deny;
            }
            foreach (var value in GuardrailRuleHelpers.ArgumentValues(request))
            {
                foreach (var pattern in patterns)
                {
                    bool matched;
                    try { matched = Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, GuardrailRuleHelpers.PatternTimeout); }
                    catch (RegexMatchTimeoutException)
                    {
                        reason = $"A forbidden-argument pattern ('{pattern}') timed out; refused.";
                        return GuardrailVerdict.Deny;
                    }
                    if (matched)
                    {
                        reason = $"Argument contains a forbidden pattern ('{pattern}').";
                        return GuardrailVerdict.Deny;
                    }
                }
            }
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}

/// <summary>Hybrid autonomy: everything above the allowed autonomous level (or any write,
/// if RequireConfirmationForWrites) needs confirmation → Confirm (not Deny).</summary>
public sealed class ConfirmationRule : IGuardrailRule
{
    public string Id => "confirmation";

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var policy = request.Context.Policy;
        var aboveAutonomous = GuardrailRuleHelpers.Rank(request.RequiredLevel)
                              > GuardrailRuleHelpers.Rank(policy.MaxAutonomousLevel);
        var writeNeedsConfirm = policy.RequireConfirmationForWrites
                                && request.RequiredLevel != McpPermissionLevels.Read;
        if (aboveAutonomous || writeNeedsConfirm)
        {
            reason = $"'{request.ToolName}' ({request.RequiredLevel}) requires confirmation.";
            return GuardrailVerdict.Confirm;
        }
        reason = "";
        return GuardrailVerdict.Allow;
    }
}
