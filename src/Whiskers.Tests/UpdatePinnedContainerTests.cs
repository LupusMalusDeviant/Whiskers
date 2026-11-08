using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Docker.Operations;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D14: a container a Whiskers compose run pinned (label <c>whiskers.image.ref</c>) is updated by
/// redeploying its project. <c>container.update</c> and AutoPatch refuse it with a clear message before anything
/// changes; the update check asks the registry about the reference the label names and compares with what runs.
/// </summary>
public sealed class UpdatePinnedContainerTests
{
    private static readonly string RunningId = "sha256:" + new string('1', 64);

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([DeploymentScripts.RepoRoot(), "src", "Whiskers", .. path])).Replace("\r\n", "\n");

    // ---------------------------------------------------------------- container.update

    [Fact]
    public void Container_update_refuses_a_pinned_container_with_the_redeploy_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContainerLifecycleOperations.RefuseIfPinned("shop-web-1",
            new Dictionary<string, string> { ["com.docker.compose.project"] = "shop", [ComposePins.ReferenceLabel] = "nginx:1.27" }));

        Assert.Equal($"Container 'shop-web-1' is {ComposePins.PinnedRefusal}.", ex.Message);
    }

    [Fact]
    public void Container_update_lets_an_unpinned_container_through()
    {
        ContainerLifecycleOperations.RefuseIfPinned("a", null);
        ContainerLifecycleOperations.RefuseIfPinned("b", new Dictionary<string, string> { ["com.docker.compose.project"] = "shop" });
        ContainerLifecycleOperations.RefuseIfPinned("c", new Dictionary<string, string> { [ComposePins.ReferenceLabel] = " " });
    }

    /// <summary>The recreate runs the guard on the inspected labels before its first change (pull, stop, rename,
    /// create). Read from the source: the test assembly loads Testcontainers' Docker.DotNet build, so the real recreate
    /// cannot run in-process.</summary>
    [Fact]
    public void The_recreate_checks_the_pin_before_its_first_change()
    {
        var source = Source("Services", "Docker", "Operations", "ContainerLifecycleOperations.cs");
        var body = source[source.IndexOf("public async Task<string> RecreateContainerAsync(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];

        var inspect = body.IndexOf("InspectContainerAsync(containerId)", StringComparison.Ordinal);
        var guard = body.IndexOf("RefuseIfPinned(name, config.Labels);", StringComparison.Ordinal);
        Assert.InRange(inspect, 0, guard);
        foreach (var change in new[] { "PullImageAsync(", "StopContainerAsync(", "RenameContainerAsync(", "CreateContainerAsync(", "RemoveContainerAsync(" })
        {
            var at = body.IndexOf(change, StringComparison.Ordinal);
            Assert.True(at < 0 || guard < at, $"the pin guard must come before {change}");
        }
    }

    // ---------------------------------------------------------------- AutoPatch

    [Fact]
    public void AutoPatch_skips_a_pinned_container_with_the_redeploy_message()
    {
        var pinned = AutoPatchTests.Compose("web", image: "docker.io/library/nginx@sha256:" + new string('a', 64),
            extra: new() { [ComposePins.ReferenceLabel] = "nginx:1.27" });
        var pinnedById = AutoPatchTests.Compose("app", image: RunningId,
            extra: new() { [ComposePins.ReferenceLabel] = "shop-app" });

        var plan = AutoPatchPlanner.Plan([pinned, pinnedById],
            [AutoPatchTests.Scan("web", "local", (CveSeverity.Critical, "1")), AutoPatchTests.Scan("app", "local", (CveSeverity.Critical, "1"))],
            new AutoPatchSettings());

        Assert.Empty(plan.Candidates);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.All(plan.Skipped, s => Assert.Equal(ComposePins.PinnedRefusal, s.Reason));
    }

    [Fact]
    public void The_host_script_aborts_a_pinned_container_before_it_touches_a_tag_or_pulls()
    {
        var script = Source("Services", "AutoPatch", "img-update.sh");
        var guard = script.IndexOf("PIN=$(lbl whiskers.image.ref)", StringComparison.Ordinal);
        var abort = script.IndexOf("UPD-ABORT pinned-by-whiskers", StringComparison.Ordinal);

        Assert.InRange(guard, 0, abort);
        foreach (var change in new[] { "docker tag ", "docker pull ", "docker image rm", "dc up", "dc pull" })
        {
            var at = script.IndexOf(change, StringComparison.Ordinal);
            Assert.True(at < 0 || abort < at, $"the pinned guard must come before '{change}'");
        }
        Assert.Matches(new Regex("^[^\n]*UPD-ABORT pinned-by-whiskers[^\n]*exit 0; fi$", RegexOptions.Multiline), script);
        Assert.Equal(AutoPatchResultKind.Aborted, AutoPatchOutcome.Parse("UPD-ABORT pinned-by-whiskers redeploy the project to update").Kind);
    }

    // ---------------------------------------------------------------- the update check

    [Fact]
    public void The_update_check_asks_about_the_pinned_reference_and_compares_with_the_running_image()
    {
        var (image, localRef) = ImageUpdateChecker.ResolveCheckRefs(
            "docker.io/library/nginx@sha256:" + new string('a', 64), null, RunningId, pinnedReference: "nginx:1.27");

        Assert.Equal("nginx:1.27", image);
        Assert.Equal(RunningId, localRef);
        Assert.False(ImageUpdateChecker.IsDigestPinned(image));
    }

    [Fact]
    public void An_id_pinned_container_is_checked_by_its_reference_too()
    {
        var (image, localRef) = ImageUpdateChecker.ResolveCheckRefs(RunningId, null, RunningId, pinnedReference: "shop-app");

        Assert.Equal("shop-app", image);
        Assert.Equal(RunningId, localRef);
    }

    [Fact]
    public void Without_the_label_the_update_check_is_unchanged()
    {
        Assert.Equal(("nginx:1.27", RunningId), ImageUpdateChecker.ResolveCheckRefs("nginx:1.27", null, RunningId));
        Assert.Equal(("nginx:1.27", RunningId), ImageUpdateChecker.ResolveCheckRefs("nginx:1.27", null, RunningId, pinnedReference: " "));
    }
}
