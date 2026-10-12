using Whiskers.Services.Vpn;
using Whiskers.Services.Vpn.Providers;

namespace Whiskers.Tests;

/// <summary>When does the in-app Tailscale provider start its own tailscaled? Starting a second daemon next to a
/// host-managed one made the host daemon restart and cut the tailnet on every Whiskers restart.</summary>
public class TailscaleDaemonDecisionTests
{
    [Theory]
    [InlineData(0, """{"BackendState":"Running"}""", true)]
    [InlineData(1, """{"BackendState":"NeedsLogin"}""", true)]
    [InlineData(1, "", false)]                                    // CLI could not reach any daemon — exits 1 too
    [InlineData(1, "failed to connect to local tailscaled", false)]
    [InlineData(127, "", false)]                                  // CLI missing
    [InlineData(0, """{"Version":"1.80"}""", false)]              // JSON, but not a status answer
    public void Daemon_responds_only_on_a_real_status_answer(int exitCode, string stdout, bool expected)
    {
        Assert.Equal(expected, TailscaleVpnProvider.DaemonResponds(new VpnProcessRunner.Result(exitCode, stdout, "")));
    }

    [Theory]
    [InlineData(true, false, false, "UseRunning")]
    [InlineData(true, true, false, "UseRunning")]   // our own daemon answers: just use it
    [InlineData(false, true, false, "HostManaged")] // host owns tailscale0: never start a second one
    [InlineData(false, true, true, "Start")]        // explicit override
    [InlineData(false, false, false, "Start")]      // nothing there: legacy in-container bring-up
    public void A_second_daemon_is_never_started_next_to_a_host_managed_tailscale(
        bool daemonResponds, bool hostInterface, bool forceLegacy, string expected)
    {
        // The enum is internal, so the expectation travels as its name.
        Assert.Equal(expected, TailscaleVpnProvider.DecideDaemon(daemonResponds, hostInterface, forceLegacy).ToString());
    }
}
