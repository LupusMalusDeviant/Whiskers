using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Tests;

/// <summary>Contract 0017 D (INV-16, INV-24): <c>get_container_env</c> masks by <see cref="Whiskers.Utils.EnvMasking"/>,
/// the rule the web UI uses — the key and credentials in a URL value — so its promise "sensitive values are masked"
/// holds for a read key.</summary>
public sealed class ContainerEnvToolTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-env-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> GetEnvAsReader(params KeyValuePair<string, string>[] env)
    {
        var svc = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(TempDir()));
        await svc.InitializeAsync();
        var key = await svc.AddApiKeyAsync("reader", McpPermissionLevels.Read);
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + key.Key;
        var docker = new FakeDocker(new ContainerInfo { Id = "abc123abc123", Name = "app", ServerId = "local" }) { Env = [.. env] };

        return await Whiskers.Mcp.Tools.ContainerTools.GetContainerEnv(new HttpContextAccessor { HttpContext = http }, svc,
            docker, "app", "local", CancellationToken.None);
    }

    [Theory]
    [InlineData("DATABASE_URL", "postgres://app:s3cr3t-pw@db:5432/app")]
    [InlineData("MYSQL_ROOT_PASSWD", "s3cr3t-pw")]
    [InlineData("SENTRY_DSN", "https://s3cr3t-pw@sentry.example/1")]
    public async Task Get_container_env_masks_for_a_read_key_what_the_web_ui_masks(string name, string value)
    {
        Assert.True(Whiskers.Utils.EnvMasking.ShouldMask(name, value));   // the UI hides it

        var text = await GetEnvAsReader(new KeyValuePair<string, string>(name, value));

        Assert.Contains(name, text);
        Assert.False(text.Contains("s3cr3t-pw", StringComparison.Ordinal),
            $"get_container_env returned a secret in clear to a read key: {text}");
    }

    /// <summary>Parity: over a table of keys and values, the tool masks a value exactly when the UI's rule does.</summary>
    [Fact]
    public async Task The_tool_masks_exactly_what_EnvMasking_masks()
    {
        KeyValuePair<string, string>[] table =
        [
            new("API_KEY", "v-api"), new("DB_PASSWORD", "v-dbpw"), new("MYSQL_ROOT_PASSWD", "v-passwd"),
            new("DB_PASS", "v-dbpass"), new("SMTP_PASS", "v-smtp"), new("MYSQL_PWD", "v-mysqlpwd"),
            new("GITHUB_TOKEN", "v-token"), new("AWS_SECRET_ACCESS_KEY", "v-aws"), new("BASIC_AUTH", "v-auth"),
            new("SENTRY_DSN", "https://v-dsn@sentry.example/1"), new("TLS_PRIVATE", "v-private"),
            new("DATABASE_URL", "postgres://app:v-url@db/app"), new("REDIS_URI", "redis://:v-redis@cache:6379"),
            new("ConnectionStrings__Db", "Host=db;Password=v-conn"), new("ODBC", "Server=db;Pwd=v-odbc;"),
            new("PWD", "/v-pwd-dir"), new("OLDPWD", "/v-oldpwd-dir"), new("TZ", "v-tz"), new("PATH", "/usr/bin:/v-path"),
            new("BASE_URL", "https://example.com:8080/v-base"), new("GREETING", "v-hello"),
        ];

        var text = await GetEnvAsReader(table);

        foreach (var (key, value) in table)
        {
            var masked = Whiskers.Utils.EnvMasking.ShouldMask(key, value);
            Assert.Contains($"  {key}=", text);
            Assert.True(masked != text.Contains($"  {key}={value}", StringComparison.Ordinal),
                $"{key}: EnvMasking says masked={masked}, the tool says otherwise:\n{text}");
        }
    }

    [Fact]
    public async Task Harmless_values_stay_readable()
    {
        var text = await GetEnvAsReader(
            new("BASE_URL", "https://example.com:8080/api"),
            new("TZ", "Europe/Berlin"),
            new("API_KEY", "k-123"));

        Assert.Contains("BASE_URL=https://example.com:8080/api", text);
        Assert.Contains("TZ=Europe/Berlin", text);
        Assert.DoesNotContain("k-123", text);
    }

    /// <summary><c>set_container_env</c> changes only the variables it sets: comments, blank lines, the other variables,
    /// their order and line endings stay; a duplicate assignment gets the new value too, a new variable comes last.</summary>
    [Fact]
    public void Setting_variables_keeps_comments_and_unrelated_lines_of_the_env_file()
    {
        const string existing =
            "# Database\r\n" +
            "DB_HOST=db\r\n" +
            "\r\n" +
            "  # keep this note\r\n" +
            "TAG=1\r\n" +
            "export-me not a var\r\n" +
            "TAG=0\r\n";

        var (content, changed) = Whiskers.Mcp.Tools.DotEnvMerge.Merge(existing, "TAG=2, NEW_KEY=x");

        Assert.Equal(
            "# Database\r\n" +
            "DB_HOST=db\r\n" +
            "\r\n" +
            "  # keep this note\r\n" +
            "TAG=2\r\n" +
            "export-me not a var\r\n" +
            "TAG=2\r\n" +
            "NEW_KEY=x\n", content);
        Assert.Equal(["TAG", "NEW_KEY"], changed);
        Assert.Equal("A=1\n", Whiskers.Mcp.Tools.DotEnvMerge.Merge(null, "A=1").Content);
    }
}
