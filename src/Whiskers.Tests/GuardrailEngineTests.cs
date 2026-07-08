using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;

namespace Whiskers.Tests;

public class GuardrailEngineTests
{
    private static readonly GuardrailEngine Engine = GuardrailEngine.CreateDefault();

    // ---- Helpers -----------------------------------------------------------

    private static AgentPrincipal Principal(string level, IReadOnlyList<string>? allowedTools = null) =>
        new(AgentPrincipalKind.WebUser, "tester", level, allowedTools, UserEmail: "t@example.com");

    private static GuardrailRequest Request(
        string tool, string requiredLevel, GuardrailPolicy policy,
        AgentPrincipal principal, params (string Key, string Value)[] args)
    {
        var dict = args.ToDictionary(
            a => a.Key,
            a => JsonSerializer.SerializeToElement(a.Value));
        var ctx = new AgentContext("sess-1", principal, AgentOrigin.WebUi, policy);
        return new GuardrailRequest(tool, requiredLevel, dict, ctx);
    }

    private static GuardrailPolicy Permissive() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Admin,
        RequireConfirmationForWrites = false,
    };

    // ---- PrincipalCeilingRule ---------------------------------------------

    [Fact]
    public void Read_tool_by_read_principal_is_allowed()
    {
        var d = Engine.Evaluate(Request("list_containers", McpPermissionLevels.Read,
            Permissive(), Principal(McpPermissionLevels.Read)));
        Assert.Equal(GuardrailVerdict.Allow, d.Verdict);
    }

    [Fact]
    public void Write_tool_by_read_principal_is_denied()
    {
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            Permissive(), Principal(McpPermissionLevels.Read)));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("principal-ceiling", d.MatchedRuleIds);
    }

    [Fact]
    public void Tool_outside_principal_allowlist_is_denied()
    {
        var d = Engine.Evaluate(Request("get_container_logs", McpPermissionLevels.Read,
            Permissive(), Principal(McpPermissionLevels.Read, allowedTools: new[] { "list_containers" })));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("principal-ceiling", d.MatchedRuleIds);
    }

    // ---- ReadOnlyModeRule --------------------------------------------------

    [Fact]
    public void ReadOnlyMode_blocks_writes_even_for_admin()
    {
        var policy = Permissive();
        policy.ReadOnlyMode = true;
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            policy, Principal(McpPermissionLevels.Admin)));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("read-only-mode", d.MatchedRuleIds);
    }

    // ---- Deny / Allow lists ------------------------------------------------

    [Fact]
    public void Deny_list_blocks_named_tool()
    {
        var policy = Permissive();
        policy.ToolDenyList.Add("execute_command");
        var d = Engine.Evaluate(Request("execute_command", McpPermissionLevels.Admin,
            policy, Principal(McpPermissionLevels.Admin)));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("tool-deny-list", d.MatchedRuleIds);
    }

    [Fact]
    public void Allow_list_blocks_everything_not_listed()
    {
        var policy = Permissive();
        policy.ToolAllowList.Add("list_containers");
        var denied = Engine.Evaluate(Request("get_server_info", McpPermissionLevels.Read,
            policy, Principal(McpPermissionLevels.Read)));
        Assert.Equal(GuardrailVerdict.Deny, denied.Verdict);

        var allowed = Engine.Evaluate(Request("list_containers", McpPermissionLevels.Read,
            policy, Principal(McpPermissionLevels.Read)));
        Assert.Equal(GuardrailVerdict.Allow, allowed.Verdict);
    }

    // ---- ProtectedResourceRule --------------------------------------------

    [Fact]
    public void Protected_resource_glob_blocks_matching_argument()
    {
        var policy = Permissive();
        policy.ProtectedResources.Add("serverwatch*");
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            policy, Principal(McpPermissionLevels.Admin), ("containerId", "serverwatch-app-1")));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("protected-resource", d.MatchedRuleIds);
    }

    [Fact]
    public void Protected_resource_does_not_block_unrelated_argument()
    {
        var policy = Permissive();
        policy.ProtectedResources.Add("serverwatch*");
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            policy, Principal(McpPermissionLevels.Admin), ("containerId", "nginx-prod")));
        Assert.Equal(GuardrailVerdict.Allow, d.Verdict);
    }

    // ---- ForbiddenArgumentRule --------------------------------------------

    [Fact]
    public void Forbidden_pattern_blocks_destructive_command()
    {
        var d = Engine.Evaluate(Request("execute_command", McpPermissionLevels.Admin,
            GuardrailPolicy.SafeDefault(), Principal(McpPermissionLevels.Admin),
            ("command", "rm -rf /")));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
        Assert.Contains("forbidden-argument", d.MatchedRuleIds);
    }

    [Fact]
    public void Invalid_policy_regex_does_not_throw()
    {
        var policy = Permissive();
        policy.ForbiddenArgPatterns.Add("[unclosed");   // ungültige Regex
        var d = Engine.Evaluate(Request("list_containers", McpPermissionLevels.Read,
            policy, Principal(McpPermissionLevels.Read), ("q", "anything")));
        Assert.Equal(GuardrailVerdict.Allow, d.Verdict);
    }

    // ---- ConfirmationRule --------------------------------------------------

    [Fact]
    public void Write_requires_confirmation_under_default_policy()
    {
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            GuardrailPolicy.SafeDefault(), Principal(McpPermissionLevels.Admin)));
        Assert.Equal(GuardrailVerdict.Confirm, d.Verdict);
        Assert.Contains("confirmation", d.MatchedRuleIds);
    }

    [Fact]
    public void Write_is_autonomous_when_policy_permits()
    {
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            Permissive(), Principal(McpPermissionLevels.Admin)));
        Assert.Equal(GuardrailVerdict.Allow, d.Verdict);
    }

    // ---- Most-restrictive aggregation -------------------------------------

    [Fact]
    public void Deny_beats_confirm()
    {
        // Write under SafeDefault → Confirm; plus a deny-listed tool → Deny must win.
        var policy = GuardrailPolicy.SafeDefault();
        policy.ToolDenyList.Add("stop_container");
        var d = Engine.Evaluate(Request("stop_container", McpPermissionLevels.Write,
            policy, Principal(McpPermissionLevels.Admin)));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
    }

    [Fact]
    public void SafeDefault_protects_serverwatch_and_blocks_forkbomb()
    {
        var policy = GuardrailPolicy.SafeDefault();
        Assert.Contains("serverwatch*", policy.ProtectedResources);
        var d = Engine.Evaluate(Request("execute_command", McpPermissionLevels.Admin,
            policy, Principal(McpPermissionLevels.Admin), ("command", ":(){ :|:& };:")));
        Assert.Equal(GuardrailVerdict.Deny, d.Verdict);
    }
}
