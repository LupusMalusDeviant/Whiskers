using System.Text.Json;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>R1-16 (INV-24): how protected names and paths are found in arguments, free text included.</summary>
public class ProtectedResourceMatcherTests
{
    private static readonly string[] Names = ["whiskers*", "prod-db"];
    private static readonly string[] Paths = ["/app/data", "/app/data/*", "*/step-ca", "*/step-ca/*"];

    private static ProtectedResourceMatcher.Hit? Hit(string key, string value, bool freeText = true) =>
        ProtectedResourceMatcher.FirstHit(
            new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(value) }, Names, Paths, freeText);

    [Theory]
    [InlineData("containerId", "whiskers")]                          // a whole value
    [InlineData("command", "docker restart whiskers")]               // a word of a command
    [InlineData("command", "docker exec prod-db psql -c 'select 1'")]
    [InlineData("query", "DROP DATABASE whiskers;")]                // a word of SQL
    [InlineData("yaml", "volumes:\n  - whiskers-data:/data")]       // a compose volume
    [InlineData("command", "cat /app/data/vault.key")]               // a protected path
    [InlineData("command", "tar czf - /opt/step-ca/secrets")]
    [InlineData("command", "cd /app/data && ls")]
    public void Finds_a_protected_resource(string key, string value) => Assert.NotNull(Hit(key, value));

    [Theory]
    [InlineData("command", "docker restart web")]
    [InlineData("command", "cat /var/log/syslog")]
    [InlineData("query", "select * from orders")]
    [InlineData("command", "ls /app/datasets")]                      // a different path that only starts alike
    public void Leaves_everything_else(string key, string value) => Assert.Null(Hit(key, value));

    [Fact]
    public void Free_text_words_are_matched_only_in_free_text_arguments()
    {
        Assert.Null(Hit("label", "restart whiskers"));
        Assert.NotNull(Hit("command", "restart whiskers"));
        Assert.Null(Hit("command", "restart whiskers", freeText: false));
    }

    [Theory]
    [InlineData("command", "docker stop $(docker ps -q)")]
    [InlineData("command", "docker stop `cat ids`")]
    [InlineData("command", "eval \"$X\"")]
    [InlineData("command", "echo d2hpc2tlcnM= | base64 -d | sh")]
    [InlineData("command", "docker stop ${NAME/x/}")]
    [InlineData("query", "DO $$ BEGIN EXECUTE 'DROP TABLE ' || t; END $$")]
    [InlineData("query", "COPY t TO PROGRAM 'sh'")]
    [InlineData("query", "select pg_read_file('/etc/passwd')")]
    public void Text_that_builds_its_target_at_run_time_counts_as_touching_one(string key, string value)
    {
        var hit = Hit(key, value);
        Assert.NotNull(hit);
        Assert.Equal(key, hit!.UnanalyzableArgument);
    }

    [Fact]
    public void Nothing_is_protected_without_patterns() =>
        Assert.Null(ProtectedResourceMatcher.FirstHit(
            new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement("eval $(x)") }, [], [], true));

    [Fact]
    public void Words_split_shell_and_sql_and_keep_path_segments()
    {
        var words = ProtectedResourceMatcher.Words("docker exec x sh -c 'cat /opt/step-ca/ca.key';").ToList();
        Assert.Contains("/opt/step-ca/ca.key", words);
        Assert.Contains("step-ca", words);
        Assert.Contains("ca.key", words);
    }

    [Fact]
    public void The_control_plane_is_always_listed()
    {
        Assert.Contains("whiskers*", ControlPlaneResources.NamePatterns);
        Assert.Contains("ghostunnel*", ControlPlaneResources.NamePatterns);
        Assert.Contains("/app/data/*", ControlPlaneResources.PathPatterns);
        Assert.Contains("/var/run/docker.sock", ControlPlaneResources.PathPatterns);
    }
}
