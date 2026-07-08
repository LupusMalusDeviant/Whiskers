using Whiskers.Services.Database;

namespace Whiskers.Tests;

public class DatabaseServiceRedisTests
{
    [Fact]
    public void ParseRedisDatabaseList_CountBecomesIndices()
    {
        // `redis-cli CONFIG GET databases` (non-TTY) prints the key name then the count on two lines.
        var result = DatabaseService.ParseRedisDatabaseList("databases\n16");

        Assert.Equal(16, result.Count);
        Assert.Equal("0", result[0]);
        Assert.Equal("15", result[^1]);
        Assert.DoesNotContain("16", result); // the count must never surface as a database name (the bug)
    }

    [Theory]
    [InlineData("")]
    [InlineData("databases\nfoo")]
    [InlineData("ERR unknown command")]
    public void ParseRedisDatabaseList_UnparseableFallsBackToSingleDb(string stdout)
    {
        Assert.Equal(new[] { "0" }, DatabaseService.ParseRedisDatabaseList(stdout));
    }

    [Fact]
    public void ParseRedisDatabaseList_SingleDatabaseConfig()
    {
        Assert.Equal(new[] { "0" }, DatabaseService.ParseRedisDatabaseList("databases\n1"));
    }
}
