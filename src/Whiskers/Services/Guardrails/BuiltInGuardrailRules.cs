using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Models.Agent;

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

/// <summary>
/// Protected resources (INV-24, INV-59, R1-16): the policy's patterns, and for every change Whiskers' own control
/// plane (<see cref="ControlPlaneResources"/>), whatever the policy says. Matched against every argument value whole,
/// against the resolved target the gateway adds, and against every word of a free-text argument (a command, SQL, a
/// compose file); free text that cannot be analysed counts as touching a protected resource.
/// A hit is refused — except a control-plane hit for an interactive Admin (the web UI or the web chat, as an Admin): their action needs an
/// explicit approval at Admin level (<see cref="Id"/> among the matched rules), which the approval gate raises,
/// binds to the call and audits.
/// </summary>
public sealed class ProtectedResourceRule : IGuardrailRule
{
    public const string RuleId = "protected-resource";

    public string Id => RuleId;

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        var policy = request.Context.Policy.ProtectedResources;
        // A read changes nothing: the control plane is protected from changes, the policy's patterns as before.
        var change = !string.Equals(McpPermissionLevels.Normalize(request.RequiredLevel), McpPermissionLevels.Read,
            StringComparison.Ordinal);
        var paths = change ? ControlPlaneResources.PathPatterns : [];
        // The policy's own patterns apply to every argument, as before. The control plane's names apply only where a
        // container, volume or network can be named — not to a server, a DNS record or a task that shares the prefix.
        var hit = ProtectedResourceMatcher.FirstHit(request.Arguments, policy, paths, analyseFreeText: change)
                  ?? (change
                      ? ProtectedResourceMatcher.FirstHit(request.Arguments, ControlPlaneResources.NamePatterns, [],
                          analyseFreeText: true, ControlPlaneResources.NamesApplyTo(request.Arguments))
                      : null);
        // A capability that changes the control plane by what it is, not by what it names (mTLS material, a restore
        // of Whiskers' own data).
        if (hit is null && change && ControlPlaneResources.Capabilities.Contains(request.ToolName))
            hit = new ProtectedResourceMatcher.Hit(request.ToolName, "control plane") { Argument = "capability" };
        if (hit is null)
        {
            reason = "";
            return GuardrailVerdict.Allow;
        }
        // A word of a payload (a command, SQL) is never quoted: it could be part of a credential (R1-16).
        reason = hit.UnanalyzableArgument is { } argument
            ? $"The '{argument}' cannot be checked for protected resources (it builds what it acts on at run time)."
            : hit.InFreeText
                ? $"The '{hit.Argument}' touches a protected resource (pattern '{hit.Pattern}')."
                : $"'{hit.Value}' is a protected resource (pattern '{hit.Pattern}').";
        // INV-59's exception covers the control plane (its names, its paths, unanalysable free text, the capabilities
        // that change it). A resource protected only by the policy's own pattern stays refused for everyone, as before.
        var controlPlane = hit.Pattern is null
                           || ControlPlaneResources.NamePatterns.Contains(hit.Pattern, StringComparer.OrdinalIgnoreCase)
                           || ControlPlaneResources.PathPatterns.Contains(hit.Pattern, StringComparer.Ordinal)
                           || hit.Pattern == "control plane"
                           || !policy.Contains(hit.Pattern, StringComparer.Ordinal);
        if (controlPlane && IsInteractiveAdmin(request.Context))
        {
            reason += " An Admin may approve it explicitly.";
            return GuardrailVerdict.Confirm;
        }
        return GuardrailVerdict.Deny;
    }

    /// <summary>INV-59's one exception: a person acting interactively — in the web UI or the web chat — as an Admin.
    /// Not a key, not an automation, not an AI trigger, not an agent instructed over MCP.</summary>
    public static bool IsInteractiveAdmin(AgentContext context) =>
        context.Origin == AgentOrigin.WebUi
        && context.Principal.Kind == AgentPrincipalKind.WebUser
        && string.Equals(McpPermissionLevels.Normalize(context.Principal.PermissionLevel), McpPermissionLevels.Admin,
            StringComparison.Ordinal);
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
