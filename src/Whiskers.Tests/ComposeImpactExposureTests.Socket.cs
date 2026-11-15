using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>Contract 0017, A1: the re-check after <c>up</c> reads each created container's <c>Mounts</c>; a bind of the
/// Docker socket the plan did not decide is an undecided exposure of class <c>host</c>.</summary>
public sealed partial class ComposeImpactExposureTests
{
    // The inspect document is built as "...,\"HostConfig\":" + this + "}": the Mounts follow the host configuration.
    private const string SocketBind =
        """{"NetworkMode":"shop_default"},"Mounts":[{"Type":"bind","Source":"/var/run/docker.sock","Destination":"/var/run/docker.sock","RW":true}]""";

    [Fact]
    public async Task A_re_check_reports_an_undecided_socket_bind_and_stops_it_under_the_agent_rule_set()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", SocketBind, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains("Docker socket", StringComparison.Ordinal) && v.Contains("'host'", StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "new-web"), ("stop", "new-web")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
    }

    [Fact]
    public async Task For_persons_an_undecided_socket_bind_keeps_running_and_is_reported()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", SocketBind, ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("Docker socket", StringComparison.Ordinal));
        Assert.Empty(_docker.Actions);
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
    }

    [Fact]
    public async Task A_socket_bind_the_plan_decided_passes_the_re_check()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    use_api_socket: true\n",
            """{"services":{"web":{"image":"nginx","use_api_socket":true}}}""");
        Assert.Contains(ComposeModel.DockerSocket, plan.Mounts());
        _upCreates.Add(("new-web", "web", SocketBind, ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.DoesNotContain(result.Violations, v => v.Contains("Docker socket", StringComparison.Ordinal));
    }
}
