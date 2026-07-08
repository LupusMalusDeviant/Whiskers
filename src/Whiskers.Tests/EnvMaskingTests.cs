using Whiskers.Utils;

namespace Whiskers.Tests;

public class EnvMaskingTests
{
    [Theory]
    [InlineData("API_KEY", "abc", true)]                         // sensitive keyword
    [InlineData("DB_PASSWORD", "x", true)]                       // keyword
    [InlineData("MY_SECRET_TOKEN", "x", true)]                   // keyword
    [InlineData("DATABASE_URL", "postgres://u:p@h/db", true)]    // credentials in value
    [InlineData("REDIS_URI", "redis://:pw@h:6379", true)]        // empty-user credentials
    [InlineData("MONGO", "mongodb://user:pass@host", true)]      // value credentials, plain key
    [InlineData("PATH", "/usr/bin:/bin", false)]                 // no keyword, no credentials
    [InlineData("BASE_URL", "https://example.com:8080/api", false)] // port (not creds); URL is not a keyword
    [InlineData("GREETING", "hello", false)]
    [InlineData("HOST", "db.internal", false)]
    public void ShouldMask(string key, string value, bool expected)
        => Assert.Equal(expected, EnvMasking.ShouldMask(key, value));

    [Fact]
    public void NullValue_MasksOnlyBySensitiveKey()
    {
        Assert.True(EnvMasking.ShouldMask("SECRET", null));
        Assert.False(EnvMasking.ShouldMask("NAME", null));
    }
}
