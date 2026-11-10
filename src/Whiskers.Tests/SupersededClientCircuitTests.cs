using System.Net.Http;
using System.Runtime.CompilerServices;
using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// A client Whiskers disposes and replaces on purpose must not count against the host (2026-11-10, Infomaniak).
///
/// <para>The mTLS switch check calls <see cref="DockerConnectionManager.InvalidateClient"/> to rebuild the
/// connection with the new credential. Every call still running on the old client then fails with an
/// <see cref="ObjectDisposedException"/>. Counted as host failures, five of them opened the circuit, and the check's
/// own ping was refused by it: the migration stopped in phase "switch" against a healthy host.</para>
/// </summary>
public class SupersededClientCircuitTests
{
    private static readonly ServerConfig Local = new()
    {
        Id = "badwolf",
        Name = "Badwolf",
        IsDefault = true,
        Enabled = true,
        ConnectionType = ConnectionType.Local
    };

    // Distinct instances without a Docker endpoint; never used for a call and never disposed.
    private static DockerClient FakeClient() => (DockerClient)RuntimeHelpers.GetUninitializedObject(typeof(DockerClient));

    private static ObjectDisposedException Disposed() => new("System.Net.Http.HttpClient");

    // ---- the rule --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dispose_on_a_client_that_was_replaced_is_superseded()
    {
        var old = FakeClient();
        Assert.True(DockerConnectionManager.IsSupersededClientFailure(Disposed(), old, FakeClient()));
    }

    [Fact]
    public void A_dispose_on_a_client_that_was_dropped_and_not_yet_rebuilt_is_superseded()
    {
        Assert.True(DockerConnectionManager.IsSupersededClientFailure(Disposed(), FakeClient(), current: null));
    }

    [Fact]
    public void A_dispose_wrapped_in_another_exception_is_found()
    {
        var wrapped = new HttpRequestException("send failed", Disposed());
        Assert.True(DockerConnectionManager.IsSupersededClientFailure(wrapped, FakeClient(), FakeClient()));
    }

    [Fact]
    public void A_dispose_on_the_client_that_is_still_cached_still_counts()
    {
        // Nothing replaced it, so it was not Whiskers' rebuild: the failure keeps counting.
        var same = FakeClient();
        Assert.False(DockerConnectionManager.IsSupersededClientFailure(Disposed(), same, same));
    }

    [Fact]
    public void Other_transport_failures_after_a_replacement_still_count()
    {
        // A replaced client is no excuse for a host that refuses connections: only the dispose itself is exempt.
        Assert.False(DockerConnectionManager.IsSupersededClientFailure(
            new HttpRequestException("Connection refused"), FakeClient(), FakeClient()));
        Assert.False(DockerConnectionManager.IsSupersededClientFailure(new TimeoutException(), FakeClient(), null));
    }

    // ---- the wiring ------------------------------------------------------------------------------------------------
    // The test client source bypasses the cache, so for these calls no client is cached: the "dropped, not yet
    // rebuilt" case. That is enough to prove the circuit consults the rule on both paths.

    private static (DockerConnectionManager Docker, ServerCircuitBreaker Circuit) Build()
    {
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());
        var servers = new FakeServerConfig(Local);
        var circuit = new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
            NullLogger<ServerCircuitBreaker>.Instance);
        var docker = new DockerConnectionManager(
            servers,
            sshTunnelManager: null!,
            new ServerBudget(settings, NullLogger<ServerBudget>.Instance),
            circuit,
            new LoopSuspensionService(new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance,
                new NoOutcomes()),
            NullLogger<DockerConnectionManager>.Instance)
        {
            ClientSourceForTests = _ => Task.FromResult(FakeClient()),
        };
        return (docker, circuit);
    }

    private static int Threshold => new ServerBudgetSettings().CircuitFailureThreshold;

    [Fact]
    public async Task Guarded_calls_on_a_replaced_client_do_not_open_the_circuit()
    {
        var (docker, circuit) = Build();

        for (var i = 0; i < Threshold + 2; i++)
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                docker.ExecuteGuardedAsync<bool>("badwolf", _ => throw Disposed()));

        var snapshot = circuit.Snapshot("badwolf");
        Assert.Equal(ServerCircuitState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task Guarded_calls_that_the_host_refuses_still_open_the_circuit()
    {
        var (docker, circuit) = Build();

        for (var i = 0; i < Threshold; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                docker.ExecuteGuardedAsync<bool>("badwolf", _ => throw new HttpRequestException("Connection refused")));

        Assert.Equal(ServerCircuitState.Open, circuit.Snapshot("badwolf").State);
    }

    [Fact]
    public async Task The_retry_path_does_not_count_a_replaced_client_either()
    {
        // ExecuteAsync retries a connection failure once on a fresh client. When that one is replaced too (two
        // rebuilds in a row), its dispose is just as little the host's doing.
        var (docker, circuit) = Build();

        for (var i = 0; i < Threshold + 2; i++)
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                docker.ExecuteAsync<bool>("badwolf", _ => throw Disposed()));

        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
    }

    [Fact]
    public async Task The_retry_path_still_counts_a_refusing_host()
    {
        var (docker, circuit) = Build();

        for (var i = 0; i < Threshold; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                docker.ExecuteAsync<bool>("badwolf", _ => throw new HttpRequestException("Connection refused")));

        Assert.Equal(ServerCircuitState.Open, circuit.Snapshot("badwolf").State);
    }
}
