using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// Telling four states apart that all look like silence (Plan-0005 WP5).
///
/// <para>Three of the four produce no findings and only one of them is good news. Collapsing them into
/// "quiet" is what the 2026-08-26 incident ran on for six days — so these tests care less about the labels
/// than about the states never being confused with each other.</para>
/// </summary>
public class ServerMonitoringStatusTests
{
    private static readonly ServerConfig HostA = new()
    {
        Id = "host-a", Name = "Host-A", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local
    };

    private static (ILoopSuspensionService Suspension, IServerCircuitBreaker Circuit) Build()
    {
        var servers = new FakeServerConfig(HostA);
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(
            new ServerBudgetSettings { CircuitFailureThreshold = 2, CircuitCooldownSeconds = 600 });

        return (
            new LoopSuspensionService(new FakeNotifications(), servers,
                NullLogger<LoopSuspensionService>.Instance, new NoOutcomes()),
            new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
                NullLogger<ServerCircuitBreaker>.Instance));
    }

    [Fact]
    public void A_server_being_checked_says_that_silence_can_be_trusted()
    {
        var (suspension, circuit) = Build();

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: true, DateTime.UtcNow);

        Assert.Equal(ServerMonitoringState.Monitored, status.State);
        Assert.Contains("nothing to find", status.Meaning);
    }

    [Fact]
    public void A_paused_server_is_never_reported_as_healthy()
    {
        // The assertion the whole package rests on. A pause produces exactly the same emptiness as a healthy
        // server, and reading one as the other is what let six days pass.
        var (suspension, circuit) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "looking at something");

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: true, DateTime.UtcNow);

        Assert.Equal(ServerMonitoringState.Paused, status.State);
        Assert.NotEqual(ServerMonitoringState.Monitored, status.State);
        Assert.Contains("not the same as nothing being wrong", status.Meaning);
    }

    [Fact]
    public void A_throttled_server_is_told_apart_from_a_paused_one()
    {
        // Both mean "not being checked" and they need different responses: one has an operator behind it who
        // knows why, the other happened on its own and nobody has looked yet.
        var (suspension, circuit) = Build();
        circuit.RecordFailure("host-a", new TimeoutException("no answer"));
        circuit.RecordFailure("host-a", new TimeoutException("no answer"));

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: true, DateTime.UtcNow);

        Assert.Equal(ServerMonitoringState.Throttled, status.State);
        Assert.Contains("nobody chose that", status.Meaning);
    }

    [Fact]
    public void An_unreachable_server_is_told_apart_from_both()
    {
        // The one case where the silence really is about the server. Labelling the other two this way would
        // send somebody to debug a host that is perfectly fine.
        var (suspension, circuit) = Build();

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: false, DateTime.UtcNow);

        Assert.Equal(ServerMonitoringState.Unreachable, status.State);
        Assert.Contains("really is about the server", status.Meaning);
    }

    [Fact]
    public void A_deliberate_pause_outranks_the_symptoms_it_causes()
    {
        // A paused server stops answering the checks, so it would also look unreachable and its circuit would
        // eventually open. Reporting those as faults would have an operator debugging their own switch.
        var (suspension, circuit) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddHours(1), "maintenance");
        circuit.RecordFailure("host-a", new TimeoutException("no answer"));
        circuit.RecordFailure("host-a", new TimeoutException("no answer"));

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: false, DateTime.UtcNow);

        Assert.Equal(ServerMonitoringState.Paused, status.State);
    }

    [Fact]
    public void A_self_imposed_pause_is_readable_as_such()
    {
        // If an automatic pause looks like the operator's own, they stop looking for a cause.
        var (suspension, circuit) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "5 failures in a row", automatic: true);

        var status = ServerMonitoring.Describe("host-a", suspension, circuit, answeredLastCycle: true, DateTime.UtcNow);

        Assert.Contains("Whiskers paused itself", status.Detail);
    }

    [Fact]
    public void The_remaining_time_is_shown_and_an_open_ended_pause_says_so()
    {
        // "in 3652 days" is what an until-revoked pause looks like if the far-future deadline is rendered
        // literally — technically true and useless.
        Assert.Equal("30 min left", ServerMonitoring.Remaining(TimeSpan.FromMinutes(30)));
        Assert.Equal("4 h left", ServerMonitoring.Remaining(TimeSpan.FromHours(4)));
        Assert.Equal("until revoked", ServerMonitoring.Remaining(TimeSpan.FromDays(3652)));
        Assert.Equal("", ServerMonitoring.Remaining(null));
    }

    [Fact]
    public void The_open_ended_option_is_not_the_first_one_offered()
    {
        // The common case is "let me look at this for ten minutes". Putting the open-ended choice at the top
        // of the list would make the one that needs a reminder into the accidental default.
        Assert.Equal(TimeSpan.FromMinutes(15), ServerMonitoring.PauseOptions[0].Duration);
        Assert.Null(ServerMonitoring.PauseOptions[^1].Duration);
    }
}
