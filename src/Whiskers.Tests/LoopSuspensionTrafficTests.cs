using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// The emergency stop has to stop actual traffic, not just a counter (Plan-0005 WP1).
///
/// <para>A switch that flips a flag while the loops keep hammering the host is the worst of both worlds: the
/// operator believes they have stopped it and looks elsewhere for the cause. So the check sits at
/// <see cref="DockerConnectionManager.GetClientAsync"/> — the one point every Docker call passes through —
/// and these tests prove it fires there, and only for background work.</para>
/// </summary>
public class LoopSuspensionTrafficTests
{
    private static readonly ServerConfig Local = new()
    {
        Id = "host-a",
        Name = "Host-A",
        IsDefault = true,
        Enabled = true,
        ConnectionType = ConnectionType.Local
    };

    private static (DockerConnectionManager Docker, ILoopSuspensionService Suspension, IServerBudget Budget) Build()
    {
        var servers = new FakeServerConfig(Local);
        var suspension = new LoopSuspensionService(
            new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance, new NoOutcomes());
        var budget = new ServerBudget(
            new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings()),
            NullLogger<ServerBudget>.Instance);
        var docker = new DockerConnectionManager(
            servers,
            sshTunnelManager: null!,
            budget,
            new ServerCircuitBreaker(
                new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings()),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<ServerCircuitBreaker>.Instance),
            suspension,
            NullLogger<DockerConnectionManager>.Instance);
        return (docker, suspension, budget);
    }

    [Fact]
    public async Task A_paused_server_turns_background_loops_away()
    {
        // The assertion that matters: the switch has to ANSWER, not merely be present. If this test ever goes
        // green with the gate removed, the emergency stop is decoration.
        var (docker, suspension, budget) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "flattening the host");

        using (budget.BackgroundScope())
        {
            var refused = await Assert.ThrowsAsync<ServerSuspendedException>(() => docker.GetClientAsync("host-a"));
            Assert.Equal("host-a", refused.ServerId);
        }
    }

    [Fact]
    public async Task An_operator_can_still_look_at_the_server_they_paused()
    {
        // Pausing must not blind the person who pressed the button — they paused it in order to look at it.
        // Whatever else happens here (there is no Docker socket in a test run), it must not be the pause.
        var (docker, suspension, _) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "flattening the host");

        try
        {
            await docker.GetClientAsync("host-a");
        }
        catch (ServerSuspendedException)
        {
            Assert.Fail("Interactive access was refused: pausing a server must not hide it from its operator.");
        }
        catch
        {
            // Anything else is the missing Docker endpoint, which is not what this test is about.
        }
    }

    [Fact]
    public async Task Once_the_pause_lapses_the_loops_come_back_by_themselves()
    {
        // Without this, every pause needs a person to remember it — and the ones that get forgotten are
        // exactly the servers nobody is watching.
        var (docker, suspension, budget) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMilliseconds(-1), "over already");

        using (budget.BackgroundScope())
        {
            try
            {
                await docker.GetClientAsync("host-a");
            }
            catch (ServerSuspendedException)
            {
                Assert.Fail("An expired pause still turned a background loop away.");
            }
            catch
            {
                // Missing Docker endpoint — not this test's subject.
            }
        }
    }
}
