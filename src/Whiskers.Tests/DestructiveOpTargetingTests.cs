using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Services.AutoUpdate;

namespace Whiskers.Tests;

// Destructive-op target-resolution helpers for Bean Whiskers-gny5 (pure logic, no HTTP/DI stubs):
// MIT-22 snapshot-only delete (here) and MIT-20 policy match (added next).
public class DestructiveOpTargetingTests
{
    // ---------------------------------------------------------------- MIT-22: only snapshots deletable

    [Theory]
    [InlineData("snapshot", true)]
    [InlineData("backup", false)]   // a backup must never be deleted by hetzner_delete_snapshot
    [InlineData("system", false)]
    [InlineData("app", false)]
    public void IsDeletableSnapshot_only_true_for_type_snapshot(string type, bool expected)
        => Assert.Equal(expected, HetznerTools.IsDeletableSnapshot(new HetznerImage { Type = type }));

    [Fact]
    public void IsDeletableSnapshot_false_for_null_image() // a not-found (404→null) image is refused
        => Assert.False(HetznerTools.IsDeletableSnapshot(null));

    // ---------------------------------------------------------------- MIT-20: policy match (ServerId-scoped)

    private static ContainerInfo Cont(string id, string name, string serverId) => new() { Id = id, Name = name, ServerId = serverId };
    private static UpdatePolicyEntity Pol(string id, string name, string? serverId) => new() { ContainerId = id, ContainerName = name, ServerId = serverId };

    [Fact]
    public void MatchesPolicy_requires_same_server()
    {
        var c = Cont("c1", "nginx", "local");
        Assert.True(AutoUpdateService.MatchesPolicy(c, Pol("c1", "nginx", "local")));
        Assert.False(AutoUpdateService.MatchesPolicy(c, Pol("c1", "nginx", "remote"))); // same name/id, other host
    }

    [Fact]
    public void MatchesPolicy_empty_policy_server_matches_any_host()
    {
        var c = Cont("c1", "nginx", "local");
        Assert.True(AutoUpdateService.MatchesPolicy(c, Pol("c1", "nginx", null)));
        Assert.True(AutoUpdateService.MatchesPolicy(c, Pol("c1", "nginx", "")));
    }

    [Fact]
    public void MatchesPolicy_matches_by_id_or_name_on_the_same_server()
    {
        var c = Cont("abc123", "nginx", "local");
        Assert.True(AutoUpdateService.MatchesPolicy(c, Pol("abc123", "other", "local")));   // by id
        Assert.True(AutoUpdateService.MatchesPolicy(c, Pol("other", "nginx", "local")));     // by name
        Assert.False(AutoUpdateService.MatchesPolicy(c, Pol("other", "other", "local")));    // neither
    }
}
