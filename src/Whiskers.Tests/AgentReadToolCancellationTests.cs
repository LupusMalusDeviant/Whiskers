using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (Medium, INV-40): a read tool the agent runs gets the caller's token. The read path ran every tool on
/// CancellationToken.None, so a closed chat or a stopped trigger left its reads running against the hosts.
/// </summary>
public class AgentReadToolCancellationTests
{
    [McpServerToolType]
    public sealed class TokenProbeTools
    {
        public static CancellationToken Seen { get; private set; }

        // Named like a real read tool: only tools of the permission registry are exposed to the agent.
        [McpServerTool]
        public static Task<string> ListContainers(CancellationToken cancellationToken)
        {
            Seen = cancellationToken;
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task A_read_tool_runs_with_the_callers_token()
    {
        var services = new ServiceCollection().AddHttpContextAccessor().BuildServiceProvider();
        var invoker = new AgentToolInvoker(new AgentToolRegistry([typeof(TokenProbeTools)]), GuardrailEngine.CreateDefault(),
            services.GetRequiredService<IServiceScopeFactory>(),
            gateway: AgentToolTestHelpers.Gateway(GuardrailPolicy.SafeDefault()));
        var context = new AgentContext("sess",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Read, null, UserEmail: "t@x"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        using var caller = new CancellationTokenSource();

        var result = await invoker.InvokeAsync(new AgentToolCall("c1", "list_containers", "{}"), context, caller.Token);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(caller.Token, TokenProbeTools.Seen);
    }
}
