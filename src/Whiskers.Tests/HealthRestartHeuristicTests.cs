using Whiskers.Services.HealthMonitor;

namespace Whiskers.Tests;

public class HealthRestartHeuristicTests
{
    [Theory]
    [InlineData("unknown", "running", false)] // flapping inspect must NOT read as a restart (the bug)
    [InlineData("exited", "running", true)]
    [InlineData("dead", "running", true)]
    [InlineData("created", "running", true)]
    [InlineData("restarting", "running", true)]
    [InlineData("running", "running", false)]
    [InlineData(null, "running", false)]      // first sight
    [InlineData("exited", "exited", false)]   // not now-running
    public void IsRestart_TruthTable(string? prev, string state, bool expected)
    {
        Assert.Equal(expected, ContainerHealthMonitor.IsRestart(prev, state));
    }
}
