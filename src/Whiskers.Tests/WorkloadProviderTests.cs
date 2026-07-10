using Whiskers.Models;
using Whiskers.Services.Workloads;

namespace Whiskers.Tests;

/// <summary>Track B.1 — the backend-neutral workload seam. Covers the capability flags, the fake
/// provider's semantics (it underpins future demo mode + consumer tests), and the factory contract.</summary>
public class WorkloadProviderTests
{
    private static ContainerInfo Workload(string id, string name, string state = "running") => new()
    {
        Id = id, Name = name, Image = "nginx:latest", State = state, Status = state,
        ServerId = "srv-1", ServerName = "Server 1",
    };

    // --- Capabilities ------------------------------------------------------------------------------

    [Fact]
    public void Docker_capabilities_support_everything()
    {
        var c = WorkloadCapabilities.Docker;
        Assert.True(c.SupportsCompose);
        Assert.True(c.SupportsHostShell);
        Assert.True(c.SupportsNetworks);
        Assert.True(c.SupportsResourceEdit);
        Assert.True(c.SupportsStats);
        Assert.Equal("container", c.StartStopSemantics);
    }

    [Fact]
    public void Kubernetes_capabilities_exclude_docker_only_features_and_are_honest_about_scale()
    {
        var c = WorkloadCapabilities.Kubernetes;
        Assert.False(c.SupportsCompose);
        Assert.False(c.SupportsHostShell);
        Assert.False(c.SupportsNetworks);
        Assert.False(c.SupportsResourceEdit);
        Assert.Equal("scale", c.StartStopSemantics);
    }

    // --- Fake provider (test/demo double) ------------------------------------------------------------

    [Fact]
    public async Task Fake_lists_all_or_running_only()
    {
        var fake = new FakeWorkloadProvider("srv-1", new[]
        {
            Workload("aaa111", "web"),
            Workload("bbb222", "worker", state: "exited"),
        });

        Assert.Equal(2, (await fake.ListWorkloadsAsync(all: true)).Count);
        var running = await fake.ListWorkloadsAsync(all: false);
        Assert.Single(running);
        Assert.Equal("web", running[0].Name);
    }

    [Fact]
    public async Task Fake_start_stop_mutate_state_and_record_calls()
    {
        var fake = new FakeWorkloadProvider("srv-1", new[] { Workload("aaa111", "web") });

        await fake.StopAsync("aaa111");
        Assert.Equal("exited", (await fake.GetWorkloadAsync("aaa111"))!.State);

        await fake.StartAsync("web"); // resolvable by name too, like the Docker paths
        Assert.Equal("running", (await fake.GetWorkloadAsync("aaa111"))!.State);

        Assert.Contains("stop:aaa111", fake.Calls);
        Assert.Contains("start:web", fake.Calls);
    }

    [Fact]
    public async Task Fake_returns_clones_so_callers_cannot_corrupt_state()
    {
        var fake = new FakeWorkloadProvider("srv-1", new[] { Workload("aaa111", "web") });
        var copy = (await fake.GetWorkloadAsync("aaa111"))!;
        copy.State = "vandalized";
        Assert.Equal("running", (await fake.GetWorkloadAsync("aaa111"))!.State);
    }

    [Fact]
    public async Task Fake_unknown_workload_throws_on_mutation_and_nulls_on_get()
    {
        var fake = new FakeWorkloadProvider("srv-1");
        Assert.Null(await fake.GetWorkloadAsync("nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.StartAsync("nope"));
    }

    [Fact]
    public async Task Fake_stats_respect_the_capability_flag()
    {
        var noStats = new FakeWorkloadProvider("srv-1", new[] { Workload("aaa111", "web") },
            WorkloadCapabilities.Kubernetes);
        Assert.Null(await noStats.GetStatsAsync("aaa111"));

        var withStats = new FakeWorkloadProvider("srv-1", new[] { Workload("aaa111", "web") });
        Assert.NotNull(await withStats.GetStatsAsync("aaa111"));
    }
}
