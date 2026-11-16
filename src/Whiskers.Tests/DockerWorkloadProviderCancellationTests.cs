using System.Reflection;
using System.Runtime.CompilerServices;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Workloads;
using Xunit;

namespace Whiskers.Tests;

/// <summary>INV-40: the Docker workload provider hands the caller's token on — a stopped caller is not left waiting
/// on a Docker read, and a change that has not started yet does not start.</summary>
public class DockerWorkloadProviderCancellationTests
{
    /// <summary>A read that ignored the token would hang on the fake; the deadline turns that into a failure.</summary>
    private static Task Within(Func<Task> call) => call().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Reads_pass_the_token_or_stop_waiting_at_it()
    {
        var docker = HangingDocker.Create(out var calls);
        var provider = new DockerWorkloadProvider("srv-1", docker);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.ListWorkloadsAsync(true, cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.GetWorkloadAsync("abc", cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.GetWorkloadIdentityAsync("abc", cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.GetLogsAsync("abc", 10, null, cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.GetStatsAsync("abc", cts.Token)));
        // The token itself reached the reads that take one.
        Assert.Contains(calls, c => c.Name == nameof(IDockerService.ListContainersAsync) && c.Token == cts.Token);
        Assert.Contains(calls, c => c.Name == nameof(IDockerService.GetContainerLogsAsync) && c.Token == cts.Token);
    }

    [Fact]
    public async Task A_change_does_not_start_for_a_stopped_caller()
    {
        var docker = HangingDocker.Create(out var calls);
        var provider = new DockerWorkloadProvider("srv-1", docker);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        // Only the token is looked at before the call; the resolver alone builds real workloads.
        var workload = (ResolvedWorkload)RuntimeHelpers.GetUninitializedObject(typeof(ResolvedWorkload));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.StartAsync(workload, cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.StopAsync(workload, cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(() => provider.RestartAsync(workload, cts.Token)));
        Assert.DoesNotContain(calls, c => c.Name is nameof(IDockerService.StartContainerAsync)
            or nameof(IDockerService.StopContainerAsync) or nameof(IDockerService.RestartContainerAsync));
    }

    /// <summary>An <see cref="IDockerService"/> whose every call waits until its token is cancelled (forever without
    /// one), recording the call and its token.</summary>
    public class HangingDocker : DispatchProxy
    {
        private readonly List<(string Name, CancellationToken Token)> _calls = [];

        public static IDockerService Create(out List<(string Name, CancellationToken Token)> calls)
        {
            var proxy = DispatchProxy.Create<IDockerService, HangingDocker>();
            calls = ((HangingDocker)(object)proxy)._calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var token = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
            _calls.Add((method!.Name, token));
            var type = method.ReturnType;
            if (type == typeof(Task))
                return Task.Delay(Timeout.Infinite, token);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(HangingDocker).GetMethod(nameof(Hang), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [token]);
            return null;
        }

        private static async Task<T> Hang<T>(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return default!;
        }
    }
}
