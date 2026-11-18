using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;
using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// The self-status MCP tool (Plan-0003 WP-MCP).
///
/// <para>Its acceptance criterion is unusually strict and worth keeping that way: <b>an agent must be able to
/// spot a stopped loop from this tool alone</b>. Not from a hint it could follow up on — from the text it
/// gets back. During the 2026-08-26 incident the log monitor had been timing out for six days and an agent
/// asked "is everything being monitored?" had no way to find out.</para>
/// </summary>
public class SelfStatusToolTests
{
    private static readonly ServerConfig HostA = new()
    {
        Id = "host-a", Name = "Host-A", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local
    };

    /// <summary>An authenticated caller in AUTH_DISABLED mode, which maps to admin — the tool is read-level,
    /// so this only gets past the permission gate to reach what the test is actually about.</summary>
    private static IHttpContextAccessor Caller()
    {
        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    Array.Empty<System.Security.Claims.Claim>(), Whiskers.Services.Auth.AuthConstants.AuthDisabledScheme))
        };
        return new HttpContextAccessor { HttpContext = context };
    }

    private static string Report(ISelfMetrics metrics, ILoopSuspensionService? suspension = null)
    {
        var servers = new FakeServerConfig(HostA);
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());

        return SelfStatusTools.GetWhiskersSelfStatus(
            Caller(),
            null!,     // unreached: the cookie branch answers before the key path
            metrics,
            new ServerBudget(settings, NullLogger<ServerBudget>.Instance),
            new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
                NullLogger<ServerCircuitBreaker>.Instance),
            suspension ?? new LoopSuspensionService(
                new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance, new NoOutcomes()),
            servers);
    }

    [Fact]
    public void A_stopped_loop_is_visible_in_the_text_alone()
    {
        // The acceptance criterion. A loop with a one-minute cadence whose last success was two hours ago.
        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        metrics.Restore("logmonitor", "host-a", DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1));

        // Restore only fills gaps, so age it the way a real stall would: a fresh instance, restored old.
        var stalled = new SelfMetrics();
        stalled.Restore("logmonitor", "host-a", DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1));

        var text = Report(stalled);

        Assert.Contains("STALLED", text);
        Assert.Contains("NEEDS ATTENTION", text);
        Assert.Contains("logmonitor", text);
    }

    [Fact]
    public void A_healthy_fleet_says_that_silence_can_be_trusted()
    {
        // The other half of the answer, and the reason the wording matters: an agent that reads "no findings"
        // needs to know whether that is good news or no news.
        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));

        var text = Report(metrics);

        Assert.DoesNotContain("STALLED", text);
        Assert.Contains("can be taken at face value", text);
    }

    [Fact]
    public void A_skipped_server_is_reported_as_skipped_and_not_as_stalled()
    {
        // A Kubernetes host under a Docker loop is not broken — but leaving it out entirely would make
        // "not covered" and "nothing found" look identical.
        var metrics = new SelfMetrics();
        metrics.RecordSkip("logmonitor", "host-a", "kubernetes");

        var text = Report(metrics);

        Assert.Contains("SKIPPED (kubernetes)", text);
        Assert.DoesNotContain("STALLED", text);
    }

    [Fact]
    public void A_loop_without_a_declared_cadence_is_shown_but_not_judged()
    {
        // No cadence, no basis for a verdict. Inventing a threshold here would make the tool a source of
        // noise, and a noisy self-check is one people stop reading.
        var metrics = new SelfMetrics();
        metrics.RecordCycle("mystery", "host-a", TimeSpan.FromMilliseconds(50), success: true);
        metrics.Restore("mystery", "host-a", DateTime.UtcNow.AddDays(-3), null);

        var text = Report(metrics);

        Assert.Contains("no declared cadence", text);
        Assert.DoesNotContain("STALLED", text);
    }

    [Fact]
    public void A_paused_server_is_named_as_paused()
    {
        // Otherwise the loops look fine and the server looks quiet, and nobody connects the two.
        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "host-a", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));

        var suspension = new LoopSuspensionService(
            new FakeNotifications(), new FakeServerConfig(HostA), NullLogger<LoopSuspensionService>.Instance, new NoOutcomes());
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "investigating");

        Assert.Contains("background checks PAUSED", Report(metrics, suspension));
    }

    [Fact]
    public void An_empty_state_is_not_reported_as_healthy()
    {
        // "Nothing recorded" after a start is ordinary; after ten minutes it means every loop is dying before
        // it can record anything. The tool must not let the first reading pass for the second.
        var text = Report(new SelfMetrics());

        Assert.Contains("No loop has recorded a cycle yet", text);
        Assert.Contains("serious fault", text);
    }
}
