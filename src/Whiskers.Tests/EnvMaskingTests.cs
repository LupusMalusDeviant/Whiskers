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
    // Contract 0017 D: PASS and PWD in a key, and connection-string password pairs in a value.
    [InlineData("DB_PASS", "x", true)]
    [InlineData("SMTP_PASS", "x", true)]
    [InlineData("MYSQL_PWD", "x", true)]
    [InlineData("MYSQL_ROOT_PASSWD", "x", true)]
    [InlineData("ConnectionStrings__Db", "Host=db;Database=app;Username=app;Password=s3cr3t", true)]
    [InlineData("ODBC", "Driver={x};Server=db;Uid=app;Pwd=s3cr3t;", true)]
    [InlineData("ConnectionStrings__Cache", "localhost:6379,abortConnect=false", false)]
    [InlineData("JDBC_OPTS", "-Djavax.net.ssl.trustStorePassword=s3cr3t", true)]
    [InlineData("ConnectionStrings__Pg", "Host=db;SslPassword=s3cr3t", true)]
    [InlineData("KAFKA_OPTS", "ssl.keyPassword=s3cr3t", true)]
    // The shell's working directories are no secret.
    [InlineData("PWD", "/app", false)]
    [InlineData("OLDPWD", "/", false)]
    public void ShouldMask(string key, string value, bool expected)
        => Assert.Equal(expected, EnvMasking.ShouldMask(key, value));

    [Fact]
    public void NullValue_MasksOnlyBySensitiveKey()
    {
        Assert.True(EnvMasking.ShouldMask("SECRET", null));
        Assert.False(EnvMasking.ShouldMask("NAME", null));
    }
}
