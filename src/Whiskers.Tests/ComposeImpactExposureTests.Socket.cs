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

    private static string Socket(string source) =>
        """{"NetworkMode":"shop_default"},"Mounts":[{"Type":"bind","Source":"SRC","Destination":"/var/run/docker.sock","RW":true}]"""
            .Replace("SRC", source);

    [Fact]
    public async Task A_planned_var_run_socket_seen_as_run_docker_sock_is_the_same_source()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n",
            """{"services":{"web":{"image":"nginx","volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock"}]}}}""");
        _upCreates.Add(("new-web", "web", Socket("/run/docker.sock"), ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.DoesNotContain(result.Violations, v => v.Contains("socket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_use_api_socket_a_rootless_engine_socket_is_decided()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    use_api_socket: true\n",
            """{"services":{"web":{"image":"nginx","use_api_socket":true}}}""");
        _upCreates.Add(("new-web", "web", Socket("/run/user/1000/docker.sock"), ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.DoesNotContain(result.Violations, v => v.Contains("socket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_socket_decided_for_another_service_is_undecided_for_this_one()
    {
        var plan = await PlanAsync(
            "name: shop\nservices:\n  web:\n    image: nginx\n  sock:\n    image: nginx\n    volumes:\n      - /var/run/docker.sock:/s\n",
            """{"services":{"web":{"image":"nginx"},"sock":{"image":"nginx","volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/s"}]}}}""");
        _upCreates.Add(("new-web", "web", Socket("/var/run/docker.sock"), ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.StartsWith("web:", StringComparison.Ordinal) && v.Contains("socket mount", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    /// <summary>Review of contract 0017: a service that only shares the network namespace of x is not labelled with
    /// x's socket mount or privileged hook — x itself is decided on them.</summary>
    [Fact]
    public async Task A_socket_mount_and_a_privileged_hook_stay_with_the_service_that_declares_them()
    {
        var plan = await PlanAsync(
            "name: shop\nservices:\n  sock:\n    image: nginx\n    use_api_socket: true\n    privileged: true\n" +
            "    post_start:\n      - command: [\"id\"]\n        privileged: true\n" +
            "  web:\n    image: nginx\n    network_mode: service:sock\n",
            """{"services":{"sock":{"image":"nginx","use_api_socket":true,"privileged":true,"post_start":[{"command":["id"],"privileged":true}]},"web":{"image":"nginx","network_mode":"service:sock"}}}""");

        var sock = plan.Services.Single(s => s.Name == "sock").Exposures;
        var web = plan.Services.Single(s => s.Name == "web").Exposures;
        Assert.Contains(sock, e => e.Kind == ExposureKind.SocketMount);
        Assert.Contains(sock, e => e is { Kind: ExposureKind.Privileged, Key: "hook:post_start:0" });
        Assert.DoesNotContain(web, ComposeExposures.IsOwnOnly);
        // What a namespace really passes on is still inherited.
        Assert.Contains(web, e => e is { Kind: ExposureKind.Privileged, Key: "privileged" });
    }

    [Fact]
    public async Task A_container_whose_mounts_cannot_be_read_counts_as_undecided()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"shop_default"},"Mounts":"?" """, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("mounts could not be read", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }
}
