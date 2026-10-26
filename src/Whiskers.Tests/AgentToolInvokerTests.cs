using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

public class AgentArgumentBinderTests
{
    private static JsonElement El(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Converts_string() =>
        Assert.Equal("nginx", AgentArgumentBinder.ConvertJson(El("\"nginx\""), typeof(string)));

    [Fact]
    public void Converts_int_from_number() =>
        Assert.Equal(100, AgentArgumentBinder.ConvertJson(El("100"), typeof(int)));

    [Fact]
    public void Converts_int_from_string() =>
        Assert.Equal(50, AgentArgumentBinder.ConvertJson(El("\"50\""), typeof(int)));

    [Fact]
    public void Converts_bool() =>
        Assert.Equal(true, AgentArgumentBinder.ConvertJson(El("true"), typeof(bool)));

    [Fact]
    public void Nullable_int_from_number() =>
        Assert.Equal(7, AgentArgumentBinder.ConvertJson(El("7"), typeof(int?)));
}

public class AgentToolInvokerTests
{
    // Scope-Factory, die meldet, ob sie benutzt wurde — und wirft, falls doch (kein Seiteneffekt erlaubt).
    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        public bool Used;
        public IServiceScope CreateScope()
        {
            Used = true;
            throw new InvalidOperationException("Es darf bei Deny/Unknown nicht ausgeführt werden.");
        }
    }

    private static AgentContext Context(string level) =>
        new("sess", new AgentPrincipal(AgentPrincipalKind.WebUser, "t", level, null, UserEmail: "t@x"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static AgentToolInvoker Invoker(TrackingScopeFactory factory) =>
        new(AgentToolTestHelpers.DefaultRegistry(), GuardrailEngine.CreateDefault(), factory,
            gateway: AgentToolTestHelpers.Gateway(GuardrailPolicy.SafeDefault()));

    [Fact]
    public async Task Unknown_tool_returns_error_without_creating_scope()
    {
        var factory = new TrackingScopeFactory();
        var result = await Invoker(factory).InvokeAsync(
            new AgentToolCall("1", "does_not_exist", "{}"), Context(McpPermissionLevels.Admin));

        Assert.True(result.IsError);
        Assert.False(factory.Used);
    }

    [Fact]
    public async Task Denied_tool_short_circuits_without_creating_scope()
    {
        var factory = new TrackingScopeFactory();
        // execute_command (admin) durch einen read-Principal → PrincipalCeiling = Deny.
        var result = await Invoker(factory).InvokeAsync(
            new AgentToolCall("2", "execute_command", "{\"command\":\"ls\"}"),
            Context(McpPermissionLevels.Read));

        Assert.True(result.IsError);
        Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
        Assert.False(factory.Used);   // niemals ausgeführt
    }

    [Fact]
    public async Task Forbidden_argument_pattern_blocks_before_execution()
    {
        var factory = new TrackingScopeFactory();
        // admin-Principal, aber destruktives Argument trifft SafeDefault-Regex → Deny.
        var result = await Invoker(factory).InvokeAsync(
            new AgentToolCall("3", "execute_command", "{\"command\":\"rm -rf /\"}"),
            Context(McpPermissionLevels.Admin));

        Assert.True(result.IsError);
        Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
        Assert.Contains("forbidden-argument", result.Decision.MatchedRuleIds);
        Assert.False(factory.Used);
    }

    [Fact]
    public async Task A_mutating_tool_needing_a_human_does_not_run_without_its_approval()
    {
        // The session normally pauses before calling the invoker; if it did not (or the approval was never
        // registered), the gateway still refuses: no approval for this operation, no execution.
        var factory = new TrackingScopeFactory();
        var result = await Invoker(factory).InvokeAsync(
            new AgentToolCall("4", "restart_container", "{\"containerId\":\"web-1\"}"),
            Context(McpPermissionLevels.Write));

        Assert.True(result.IsError);
        Assert.Equal(GuardrailVerdict.Confirm, result.Decision.Verdict);
        Assert.False(factory.Used);
    }

    [Fact]
    public async Task Without_a_gateway_a_mutating_tool_is_refused()
    {
        var factory = new TrackingScopeFactory();
        var invoker = new AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(), GuardrailEngine.CreateDefault(), factory);

        var result = await invoker.InvokeAsync(
            new AgentToolCall("5", "start_container", "{\"containerId\":\"web-1\"}"), Context(McpPermissionLevels.Admin));

        Assert.True(result.IsError);
        Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
        Assert.False(factory.Used);
    }
}
