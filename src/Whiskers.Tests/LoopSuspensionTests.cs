using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// The emergency stop and, more importantly, its limits (Plan-0005 WP0/WP1).
///
/// <para>When Whiskers was flattening a host on 2026-08-26, the fix went over SSH on the affected server —
/// past the tool that was causing the problem, because the tool had no way to take itself back. This service
/// is that way back.</para>
///
/// <para>It also introduces a new danger, and the tests weigh it heavier than the feature: a paused server
/// that looks like a healthy one is worse than no pause at all. Every pause is announced, expires on its own,
/// and the supervisory rule that reports the absence of checks must NOT be pausable by it.</para>
/// </summary>
public class LoopSuspensionTests
{
    private static (LoopSuspensionService Service, FakeNotifications Sent) Build()
    {
        var sent = new FakeNotifications();
        return (new LoopSuspensionService(
            sent,
            new FakeServerConfig(new ServerConfig { Id = "host-a", Name = "Host-A", IsDefault = true }),
            NullLogger<LoopSuspensionService>.Instance, new NoOutcomes()), sent);
    }

    [Fact]
    public void A_pause_takes_effect_and_is_announced()
    {
        var (service, sent) = Build();
        Assert.False(service.IsSuspended("host-a"));

        service.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "investigating high load");

        Assert.True(service.IsSuspended("host-a"));

        // Never silent. An unannounced pause turns "quiet" into "blind" and hides the next incident behind
        // the fix for the last one.
        var announcement = Assert.Single(sent.Events);
        Assert.Equal("loops_paused", announcement.EventType);
        Assert.Contains("not the same as nothing being wrong", announcement.ImageInfo);
    }

    [Fact]
    public void A_pause_lapses_on_its_own()
    {
        // A pause that must be revoked by hand is a pause that gets forgotten, and a forgotten pause is an
        // unmonitored server nobody remembers creating.
        var (service, sent) = Build();
        service.Suspend("host-a", DateTime.UtcNow.AddMilliseconds(-1), "already over");

        Assert.False(service.IsSuspended("host-a"));
        Assert.Equal(new[] { "loops_paused", "loops_resumed" }, sent.Events.Select(e => e.EventType));
    }

    [Fact]
    public void An_automatic_pause_is_distinguishable_from_a_click()
    {
        // If a self-imposed pause looks like the operator's own, they stop looking for a cause.
        var (service, sent) = Build();
        service.Suspend("host-a", DateTime.UtcNow.AddMinutes(5), "5 failures in a row", automatic: true);

        Assert.True(Assert.Single(service.Current()).Automatic);
        Assert.Contains("by itself", Assert.Single(sent.Events).ImageInfo);
    }

    [Fact]
    public void Resuming_says_so()
    {
        var (service, sent) = Build();
        service.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "maintenance");
        service.Resume("host-a");

        Assert.False(service.IsSuspended("host-a"));
        Assert.Equal(new[] { "loops_paused", "loops_resumed" }, sent.Events.Select(e => e.EventType));
    }

    [Fact]
    public void An_unknown_server_is_not_suspended()
    {
        var (service, _) = Build();
        Assert.False(service.IsSuspended("never-heard-of"));
    }

    [Fact]
    public void The_supervisor_cannot_be_paused_by_the_emergency_stop()
    {
        // The one rule that must survive the switch. A supervisor that can be silenced by the thing it
        // supervises is a blindfold with a label on it — so this is enforced by a test, not by a comment.
        var supervisor = typeof(ScanSupervisor);

        var takesSuspension = supervisor
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(ILoopSuspensionService));

        Assert.False(takesSuspension,
            "ScanSupervisor must not depend on ILoopSuspensionService: it reports that nothing is being " +
            "checked, and the emergency stop is one of the reasons nothing is being checked.");

        var readsSuspension = supervisor
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => f.FieldType == typeof(ILoopSuspensionService));

        Assert.False(readsSuspension, "ScanSupervisor holds a suspension service — see above.");
    }
}
