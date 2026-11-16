using Whiskers.Models;
using Whiskers.Services.Database;

namespace Whiskers.Tests;

public class DatabaseDetectorTests
{
    /// <summary>A container's environment is case-sensitive (Docker's client proxy configuration sets HTTP_PROXY and
    /// http_proxy): reading it never throws, and the upper-case spelling a database image documents wins over another
    /// case, whatever the order (INV-45 review round 11, B-1).</summary>
    [Fact]
    public void An_environment_that_repeats_a_key_in_another_case_is_read_with_the_upper_case_spelling_first()
    {
        List<KeyValuePair<string, string>> env =
        [
            new("postgres_db", "lower"), new("postgres_password", "wrong"), new("HTTP_PROXY", "a"), new("http_proxy", "b"),
            new("POSTGRES_PASSWORD", "right"),
            new("POSTGRES_DB", "upper"), new("POSTGRES_USER", "app"), new("pguser", "other"),
        ];

        var creds = DatabaseDetector.ExtractCredentials(env, DatabaseType.PostgreSQL);

        Assert.Equal("upper", creds.Database);
        Assert.Equal("app", creds.User);
        Assert.Equal("right", creds.Password);   // never the lower-case one listed first
    }

    /// <summary>NEO4J_AUTH is "user/password": the password is everything after the first '/', also when it contains
    /// '/' itself; "none" (authentication off) is no password.</summary>
    [Theory]
    [InlineData("neo4j/s3cr/et/x", "neo4j", "s3cr/et/x")]
    [InlineData("neo4j/plain", "neo4j", "plain")]
    [InlineData("none", "neo4j", "")]
    public void A_neo4j_password_with_a_slash_is_kept_whole(string auth, string user, string password)
    {
        var creds = DatabaseDetector.ExtractCredentials([new("NEO4J_AUTH", auth)], DatabaseType.Neo4j);

        Assert.Equal(user, creds.User);
        Assert.Equal(password, creds.Password);
    }
}
