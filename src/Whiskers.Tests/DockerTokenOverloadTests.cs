using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Tests.ComponentTests;

namespace Whiskers.Tests;

/// <summary>Contract 0023 (batch 1): the token overloads of the container and network operations observe the action's
/// token once, before Docker is asked (the safe point), and then hand the one call on unchanged.</summary>
public sealed class DockerTokenOverloadTests
{
    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true });
    private int _connections;

    private IDockerService Docker()
    {
        var manager = new UiCreatedResourceAuditTests.Stub<IDockerConnectionManager>()
            .On(nameof(IDockerConnectionManager.GetClientAsync), _ => { _connections++; throw new InvalidOperationException("docker reached"); });
        return new DockerService(manager.Object, _servers,
            new UiCreatedResourceAuditTests.Stub<Whiskers.Services.Metrics.IPrometheusMetricsSource>().Object,
            new UiCreatedResourceAuditTests.Stub<Whiskers.Services.Observability.IFleetSnapshotCache>().Object,
            NullLogger<DockerService>.Instance);
    }

    public static TheoryData<string> Operations() =>
        ["start", "stop", "restart", "remove", "network.create", "network.remove"];

    private static Task Call(IDockerService docker, string operation, CancellationToken ct) => operation switch
    {
        "start" => docker.StartContainerAsync("c0ffee", "local", ct),
        "stop" => docker.StopContainerAsync("c0ffee", "local", ct),
        "restart" => docker.RestartContainerAsync("c0ffee", "local", ct),
        "remove" => docker.RemoveContainerAsync("c0ffee", true, "local", ct),
        "network.create" => docker.CreateNetworkAsync("back", "bridge", "local", ct),
        "network.remove" => docker.RemoveNetworkAsync("n-back", "local", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task A_stopped_token_ends_the_operation_before_Docker_is_asked(string operation)
    {
        var docker = Docker();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(docker, operation, new CancellationToken(canceled: true)));

        Assert.Equal(0, _connections);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task A_live_token_hands_the_call_on_to_Docker(string operation)
    {
        var docker = Docker();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(docker, operation, CancellationToken.None));

        Assert.Equal("docker reached", error.Message);
        Assert.Equal(1, _connections);
    }

    [Fact]
    public async Task A_safe_point_runs_its_step_to_the_end_once_it_started()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafePoint.ThenRunToEnd(new CancellationToken(canceled: true), () => Task.FromResult(1)));

        using var stop = new CancellationTokenSource();
        var ran = await SafePoint.ThenRunToEnd(stop.Token, async () =>
        {
            stop.Cancel();
            await Task.Yield();
            return 7;
        });
        Assert.Equal(7, ran);
    }
}
