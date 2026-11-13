using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Operations;
using Whiskers.Services.Guardrails;
using Whiskers.Services.LogMonitor;

namespace Whiskers.Tests;

/// <summary>Fable review (INV-40, R1-41): a caller's stop reaches a log read as its <c>callerToken</c>. Passed as the plain
/// read's token instead, the connection manager could not tell it from the server running out of time: each closed chat
/// counted as a circuit failure against the host, and a stopped half-open probe re-opened its circuit.</summary>
public class LogReadCallerTokenTests
{
    /// <summary>Records every Docker call by method and argument name; lists one container.</summary>
    public class RecordingDocker : NullProxy<IDockerService>
    {
        public List<(string Method, Dictionary<string, object?> Args)> Calls { get; } = [];

        public static (IDockerService Service, RecordingDocker Recorder) New()
        {
            var proxy = DispatchProxy.Create<IDockerService, RecordingDocker>();
            return (proxy, (RecordingDocker)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var names = method!.GetParameters().Select(p => p.Name!).ToArray();
            lock (Calls) Calls.Add((method.Name, names.Select((n, i) => (n, args![i])).ToDictionary(x => x.n, x => x.Item2)));
            var listed = new List<ContainerInfo> { new() { Id = "c1", Name = "web", State = "running", ServerId = "srv" } };
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                && method.ReturnType.GetGenericArguments()[0] is var inner && inner.IsAssignableFrom(listed.GetType()))
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [listed]);
            if (method.ReturnType == typeof(Task<string>))
                return Task.FromResult("line one\nline two");
            return base.Invoke(method, args);
        }

        public Dictionary<string, object?> LogRead() =>
            Assert.Single(Calls, c => c.Method == nameof(IDockerService.GetContainerLogsAsync)).Args;
    }

    private static void AssertCallerToken(Dictionary<string, object?> args, CancellationToken caller)
    {
        Assert.True(args.ContainsKey("callerToken"), "the log read did not use the R1-41 overload");
        Assert.Equal(caller, args["callerToken"]);
        Assert.Equal(CancellationToken.None, args["deadline"]);
    }

    [Fact]
    public async Task Get_container_logs_hands_the_callers_stop_in_as_its_caller_token()
    {
        var (docker, recorder) = RecordingDocker.New();
        var provider = new ServiceCollection().AddHttpContextAccessor().AddSingleton(docker).BuildServiceProvider();
        var invoker = new AgentToolInvoker(new AgentToolRegistry([typeof(ContainerTools)]), GuardrailEngine.CreateDefault(),
            provider.GetRequiredService<IServiceScopeFactory>(), gateway: AgentToolTestHelpers.Gateway(GuardrailPolicy.SafeDefault()));
        var context = new AgentContext("sess",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Read, null, UserEmail: "t@x"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        using var caller = new CancellationTokenSource();

        var result = await invoker.InvokeAsync(
            new AgentToolCall("c1", "get_container_logs", """{"containerId":"web","serverId":"srv"}"""), context, caller.Token);

        Assert.False(result.IsError, result.Content);
        AssertCallerToken(recorder.LogRead(), caller.Token);
    }

    /// <summary>A connection manager whose host never answers: a call waits for its caller's token and records it.</summary>
    public class HangingConnections : NullProxy<IDockerConnectionManager>
    {
        public List<CancellationToken> CallerTokens { get; } = [];

        public static (IDockerConnectionManager Service, HangingConnections Recorder) New()
        {
            var proxy = DispatchProxy.Create<IDockerConnectionManager, HangingConnections>();
            return (proxy, (HangingConnections)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is not (nameof(IDockerConnectionManager.ExecuteAsync) or nameof(IDockerConnectionManager.ExecuteGuardedAsync)))
                return base.Invoke(method, args);
            var token = (CancellationToken)args![Array.FindIndex(method.GetParameters(), p => p.Name == "callerToken")];
            lock (CallerTokens) CallerTokens.Add(token);
            var result = method.ReturnType.GetGenericArguments()[0];
            var never = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(result))!;
            var task = (Task)never.GetType().GetProperty("Task")!.GetValue(never)!;
            // The task completes only as cancelled, through WaitAsync on the caller's token.
            return typeof(Task<>).MakeGenericType(result).GetMethod("WaitAsync", [typeof(CancellationToken)])!.Invoke(task, [token]);
        }
    }

    [Fact]
    public async Task A_server_info_read_whose_caller_stops_is_cancelled_not_unreachable()
    {
        // get_server_info: the caller's stop reaches the Docker call as its callerToken (no circuit failure), and comes
        // back as a cancellation — a stopped caller is not an unreachable server.
        var (connections, recorder) = HangingConnections.New();
        var ops = new SystemInfoOperations(connections, NullProxy<Whiskers.Services.ServerConfig.IServerConfigService>.Create(),
            NullProxy<Whiskers.Services.Metrics.IPrometheusMetricsSource>.Create(), null!,
            NullLogger<DockerService>.Instance, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullProxy<Whiskers.Services.Observability.IFleetSnapshotCache>.Create());
        using var caller = new CancellationTokenSource();

        var read = ops.GetServerSystemInfoAsync("srv", caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(caller.Token, Assert.Single(recorder.CallerTokens));
    }

    [Fact]
    public async Task Search_logs_hands_the_callers_stop_in_as_its_caller_token()
    {
        var (docker, recorder) = RecordingDocker.New();
        using var caller = new CancellationTokenSource();

        await new LogSearchService(docker, NullLogger<LogSearchService>.Instance).SearchAsync("one", ct: caller.Token);

        AssertCallerToken(recorder.LogRead(), caller.Token);
    }
}
