using ServerWatch.Mcp.Tools;
using ServerWatch.Models.Hetzner;

namespace ServerWatch.Tests;

// Destructive-op target-resolution helpers for Bean ServerWatch-gny5 (pure logic, no HTTP/DI stubs):
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
}
