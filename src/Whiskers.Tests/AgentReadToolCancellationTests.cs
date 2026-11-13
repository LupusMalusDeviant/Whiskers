using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using ModelContextProtocol.Server;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (Medium, INV-40): a read tool the agent runs gets the caller's token. The read path ran every tool on
/// CancellationToken.None, so a closed chat or a stopped trigger left its reads running against the hosts. The follow-up
/// review found that no real read tool took the token at all: these tests prove that a real one stops, and that the
/// caller is released even by a tool that ignores the token.
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

    [McpServerToolType]
    public sealed class DeafTools
    {
        public static TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A read that never answers and takes no token — a tool whose service has no cancellable overload.
        [McpServerTool]
        public static Task<string> GetHealthSummary()
        {
            Started.TrySetResult();
            return new TaskCompletionSource<string>().Task;
        }
    }

    private static AgentContext ReadContext() => new("sess",
        new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Read, null, UserEmail: "t@x"),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static AgentToolInvoker Invoker(Type toolType, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection().AddHttpContextAccessor();
        services?.Invoke(collection);
        var provider = collection.BuildServiceProvider();
        return new AgentToolInvoker(new AgentToolRegistry([toolType]), GuardrailEngine.CreateDefault(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            gateway: AgentToolTestHelpers.Gateway(GuardrailPolicy.SafeDefault()));
    }

    [Fact]
    public async Task A_read_tool_runs_with_the_callers_token()
    {
        var invoker = Invoker(typeof(TokenProbeTools));
        using var caller = new CancellationTokenSource();

        var result = await invoker.InvokeAsync(new AgentToolCall("c1", "list_containers", "{}"), ReadContext(), caller.Token);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(caller.Token, TokenProbeTools.Seen);
    }

    [Fact]
    public async Task A_real_read_tool_stops_at_the_host_when_its_caller_stops()
    {
        // list_containers against a host that does not answer: only the caller's token can end the listing.
        var docker = new FakeDocker { ListContainersHangs = true };
        var invoker = Invoker(typeof(ContainerTools), s => s.AddSingleton<IDockerService>(docker));
        using var caller = new CancellationTokenSource();

        var run = invoker.InvokeAsync(new AgentToolCall("c1", "list_containers", """{"serverId":"local"}"""), ReadContext(),
            caller.Token);
        await docker.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsError, result.Content);
        // The stop reached the Docker call itself — the read does not keep running against the host.
        await docker.ListingSawStop.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_read_tool_that_ignores_the_token_still_releases_its_caller()
    {
        var invoker = Invoker(typeof(DeafTools));
        using var caller = new CancellationTokenSource();

        var run = invoker.InvokeAsync(new AgentToolCall("c1", "get_health_summary", "{}"), ReadContext(), caller.Token);
        await DeafTools.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsError, result.Content);
    }
}
