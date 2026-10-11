using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// The per-server circuit breaker (Plan-0001 WP4).
///
/// <para>Two properties matter, and the second one more than the first. Yes, a dead host must stop being
/// hammered — but a circuit that opens quietly is worse than no circuit at all: Whiskers has stopped looking
/// at that server, and silence then reads as "all quiet". That confusion is precisely what let the
/// 2026-08-26 incident run for six days, which is why "every transition is announced" is tested as hard as
/// the state machine itself.</para>
/// </summary>
public class ServerCircuitBreakerTests
{
    private static (IServerCircuitBreaker Circuit, FakeNotifications Sent) Build(
        int threshold = 3, int cooldownSeconds = 60)
    {
        var sent = new FakeNotifications();
        var services = new ServiceCollection();
        services.AddSingleton<INotificationService>(sent);
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(
            new FakeServerConfig(new ServerConfig { Id = "badwolf", Name = "Badwolf", IsDefault = true }));

        var circuit = new ServerCircuitBreaker(
            new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings
            {
                CircuitFailureThreshold = threshold,
                CircuitCooldownSeconds = cooldownSeconds
            }),
            services.BuildServiceProvider(),
            NullLogger<ServerCircuitBreaker>.Instance);

        return (circuit, sent);
    }

    private static Exception TransportFailure() => new TimeoutException("host did not answer");

    [Fact]
    public void Opens_after_the_configured_run_of_failures_and_says_so()
    {
        var (circuit, sent) = Build(threshold: 3);

        circuit.RecordFailure("badwolf", TransportFailure());
        circuit.RecordFailure("badwolf", TransportFailure());
        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
        Assert.Empty(sent.Events);   // two failures are a blip, not a verdict

        circuit.RecordFailure("badwolf", TransportFailure());

        Assert.Equal(ServerCircuitState.Open, circuit.Snapshot("badwolf").State);
        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));

        // The part that matters: nobody may have to guess why the server went quiet.
        var announcement = Assert.Single(sent.Events);
        Assert.Equal("server_throttled", announcement.EventType);
        Assert.Equal("badwolf", announcement.ServerId);
    }

    [Fact]
    public void Announces_exactly_once_no_matter_how_many_more_calls_fail()
    {
        var (circuit, sent) = Build(threshold: 2);

        for (var i = 0; i < 20; i++) circuit.RecordFailure("badwolf", TransportFailure());

        // A storm of failures is one event, not twenty. An alert channel that gets twenty is an alert
        // channel people mute.
        Assert.Single(sent.Events);
    }

    [Fact]
    public void A_success_resets_the_run()
    {
        var (circuit, sent) = Build(threshold: 3);

        circuit.RecordFailure("badwolf", TransportFailure());
        circuit.RecordFailure("badwolf", TransportFailure());
        circuit.RecordSuccess("badwolf");
        circuit.RecordFailure("badwolf", TransportFailure());
        circuit.RecordFailure("badwolf", TransportFailure());

        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
        Assert.Empty(sent.Events);
    }

    [Fact]
    public void After_the_cooldown_one_probe_gets_through_and_recovery_closes_the_circuit()
    {
        var (circuit, sent) = Build(threshold: 1, cooldownSeconds: 1);
        circuit.RecordFailure("badwolf", TransportFailure());
        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));

        Thread.Sleep(1100);

        // First caller after the cooldown becomes the probe...
        circuit.ThrowIfOpen("badwolf");
        Assert.Equal(ServerCircuitState.HalfOpen, circuit.Snapshot("badwolf").State);

        // ...and everyone else still fails fast, so a recovering host gets one request, not a stampede.
        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));

        circuit.RecordSuccess("badwolf");

        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
        circuit.ThrowIfOpen("badwolf");   // no longer throws

        // A server that comes back on its own must say so too — otherwise the operator is left believing
        // it is still down.
        Assert.Equal(new[] { "server_throttled", "server_throttling_ended" }, sent.Events.Select(e => e.EventType));
    }

    [Fact]
    public void A_failed_probe_reopens_the_circuit_without_a_second_announcement()
    {
        var (circuit, sent) = Build(threshold: 1, cooldownSeconds: 1);
        circuit.RecordFailure("badwolf", TransportFailure());
        Thread.Sleep(1100);

        circuit.ThrowIfOpen("badwolf");                        // probe
        circuit.RecordFailure("badwolf", TransportFailure());  // still broken

        Assert.Equal(ServerCircuitState.Open, circuit.Snapshot("badwolf").State);
        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));
        Assert.Single(sent.Events);   // still just the one "throttled" — a failed retry is not news
    }

    [Fact]
    public void Application_errors_do_not_open_the_circuit()
    {
        var (circuit, sent) = Build(threshold: 2);

        // "No such container" says nothing about whether the HOST is reachable. Counting it would pause a
        // perfectly healthy server because someone asked about a container that no longer exists.
        circuit.RecordFailure("badwolf", new InvalidOperationException("No such container: abc"));
        circuit.RecordFailure("badwolf", new ArgumentException("bad parameter"));
        circuit.RecordFailure("badwolf", new KeyNotFoundException());

        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
        Assert.Empty(sent.Events);
    }

    [Fact]
    public void A_probe_that_hits_an_application_error_lets_the_next_caller_probe_again()
    {
        var (circuit, _) = Build(threshold: 1, cooldownSeconds: 1);
        circuit.RecordFailure("badwolf", TransportFailure());
        Thread.Sleep(1100);

        circuit.ThrowIfOpen("badwolf");                                                     // probe
        circuit.RecordFailure("badwolf", new InvalidOperationException("No such container")); // host answered

        // Before the fix the slot stayed taken here and every later call failed fast until a restart.
        Assert.Equal(ServerCircuitState.HalfOpen, circuit.Snapshot("badwolf").State);
        circuit.ThrowIfOpen("badwolf");   // the next caller becomes the probe instead of being refused
    }

    [Fact]
    public void A_probe_released_without_a_verdict_does_not_keep_the_server_paused()
    {
        var (circuit, _) = Build(threshold: 1, cooldownSeconds: 1);
        circuit.RecordFailure("badwolf", TransportFailure());
        Thread.Sleep(1100);

        circuit.ThrowIfOpen("badwolf");   // probe, which then never reaches the host (client build threw)
        circuit.ReleaseProbe("badwolf");

        circuit.ThrowIfOpen("badwolf");   // a new probe is allowed
        circuit.RecordSuccess("badwolf");
        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("badwolf").State);
    }

    [Fact]
    public void Releasing_after_the_probe_was_settled_changes_nothing()
    {
        var (circuit, sent) = Build(threshold: 1, cooldownSeconds: 1);
        circuit.RecordFailure("badwolf", TransportFailure());
        Thread.Sleep(1100);

        circuit.ThrowIfOpen("badwolf");
        circuit.RecordFailure("badwolf", TransportFailure());   // probe failed → open again
        circuit.ReleaseProbe("badwolf");                         // the finally in the caller runs anyway

        Assert.Equal(ServerCircuitState.Open, circuit.Snapshot("badwolf").State);
        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));

        circuit.ReleaseProbe("burgcloud");                       // never seen, closed: harmless
        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("burgcloud").State);
        Assert.Single(sent.Events);
    }

    [Fact]
    public void One_broken_server_does_not_pause_the_others()
    {
        var (circuit, _) = Build(threshold: 1);

        circuit.RecordFailure("badwolf", TransportFailure());

        Assert.Throws<ServerCircuitOpenException>(() => circuit.ThrowIfOpen("badwolf"));
        circuit.ThrowIfOpen("burgcloud");   // untouched
        Assert.Equal(ServerCircuitState.Closed, circuit.Snapshot("burgcloud").State);
    }
}
