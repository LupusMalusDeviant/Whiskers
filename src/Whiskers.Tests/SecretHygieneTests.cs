using Whiskers.Utils;

namespace Whiskers.Tests;

// Secret-hygiene regression for Bean Whiskers-b9qw. DB command-builders now pass passwords via env
// vars (out of the process argv) and HostCommandExecutor redacts commands before logging — these tests
// prove the redaction those debug logs rely on actually hides DB passwords and keyed secrets.
public class SecretHygieneTests
{
    [Fact]
    public void Redact_hides_db_password_in_argv_and_env_forms()
    {
        var argv = SecretRedactor.Redact("mysql -uroot -psup3rs3cret -e 'select 1'");
        Assert.DoesNotContain("sup3rs3cret", argv);
        Assert.Contains("-p***", argv);

        var mysqlEnv = SecretRedactor.Redact("MYSQL_PWD=sup3rs3cret mysqldump db > /tmp/x.sql");
        Assert.DoesNotContain("sup3rs3cret", mysqlEnv);
        Assert.Contains("MYSQL_PWD=***", mysqlEnv);

        var pgEnv = SecretRedactor.Redact("PGPASSWORD=sup3rs3cret pg_dump -U u db");
        Assert.DoesNotContain("sup3rs3cret", pgEnv);
        Assert.Contains("PGPASSWORD=***", pgEnv);
    }

    [Theory]
    [InlineData("ALTER ROLE whiskers PASSWORD 'canary-secret';")]
    [InlineData("CREATE USER app WITH ENCRYPTED PASSWORD 'canary pw with spaces'")]
    [InlineData("ALTER ROLE app PASSWORD $$canary-secret$$")]
    [InlineData("ALTER ROLE app PASSWORD $q$canary-secret$q$")]
    [InlineData("CREATE USER 'a'@'%' IDENTIFIED BY 'it''s canary-secret';")]
    [InlineData("CREATE USER 'a'@'%' IDENTIFIED BY 'it\\'s canary-secret';")]
    [InlineData("ALTER USER a IDENTIFIED WITH caching_sha2_password BY 'canary-secret'")]
    [InlineData("CREATE USER scott IDENTIFIED BY canary-secret;")]
    [InlineData("SET PASSWORD = 'canary pw'")]
    [InlineData("SET PASSWORD FOR 'a'@'%' = PASSWORD('canary-secret')")]
    [InlineData("ALTER LOGIN sa WITH PASSWORD = 'canary-secret'")]
    [InlineData("ALTER ROLE app PASSWORD /* rotate */ 'canary-secret';")]          // comments between the tokens
    [InlineData("ALTER ROLE app PASSWORD -- rotate\n 'canary-secret';")]
    [InlineData("CREATE USER a IDENTIFIED # note\n BY 'canary-secret';")]
    [InlineData("CREATE USER scott IDENTIFIED BY /* x */ canary-secret;")]
    [InlineData("ALTER ROLE app PASSWORD E'canary-secret';")]
    [InlineData("ALTER ROLE app PASSWORD e'it\\'s canary-secret';")]
    [InlineData("ALTER ROLE app PASSWORD 'it'' s canary-secret';")]
    [InlineData("ALTER ROLE app PASSWORD 'canary-secret")]                         // unclosed: masked to the end
    [InlineData("{\"sql\":\"ALTER ROLE app PASSWORD 'canary-secret'\",\"serverId\":\"local\"}")]
    public void Redact_hides_sql_password_literals(string sql)
    {
        var s = SecretRedactor.Redact(sql);
        Assert.DoesNotContain("canary", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Redact_keeps_the_rest_of_a_sql_statement()
    {
        Assert.Equal("{\"sql\":\"ALTER ROLE app PASSWORD '***'\",\"serverId\":\"local\"}",
            SecretRedactor.Redact("{\"sql\":\"ALTER ROLE app PASSWORD 'canary-secret'\",\"serverId\":\"local\"}"));
        Assert.Equal("SELECT name FROM users WHERE id = 1", SecretRedactor.Redact("SELECT name FROM users WHERE id = 1"));
    }

    [Fact]
    public void Redact_hides_keyed_secrets()
    {
        var s = SecretRedactor.Redact("deploy --token=abc123def --password=hunter2");
        Assert.DoesNotContain("abc123def", s);
        Assert.DoesNotContain("hunter2", s);
    }

    [Fact]
    public void Redact_hides_the_rescue_password_in_german_tool_output()
    {
        // Exactly what hetzner_enable_rescue returns — it reached the agent transcript and the MCP result log.
        var s = SecretRedactor.Redact("Rescue-Mode für web aktiviert.\nTemporäres root-Passwort: Xk9fQ2mPz7");
        Assert.DoesNotContain("Xk9fQ2mPz7", s);
        Assert.Contains("Rescue-Mode für web aktiviert.", s);
    }

    [Fact]
    public void Redact_hides_secrets_in_json()
    {
        var s = SecretRedactor.Redact("{\"user\":\"bw\",\"root_password\": \"Xk9fQ2mPz7\",\"apiKey\":\"placeholder-value\"}");
        Assert.DoesNotContain("Xk9fQ2mPz7", s);
        Assert.DoesNotContain("placeholder-value", s);
        Assert.Contains("\"user\":\"bw\"", s);
    }

    [Fact]
    public void Redact_hides_bearer_tokens_and_url_credentials()
    {
        var bearer = SecretRedactor.Redact("curl -H 'Authorization Bearer placeholderTOKEN123' https://x");
        Assert.DoesNotContain("placeholderTOKEN123", bearer);

        var url = SecretRedactor.Redact("git clone https://deploy:placeholderpw@gitlab.example.org/a/b.git");
        Assert.DoesNotContain("placeholderpw", url);
        Assert.Contains("https://deploy:***@gitlab.example.org/a/b.git", url);
    }

    [Fact]
    public void Redact_hides_long_base64_blobs_but_not_image_digests()
    {
        var blob = new string('A', 120) + "QUJD" + new string('z', 120);
        var b64 = SecretRedactor.Redact($"echo '{blob}' | base64 -d > /certs/server.key");
        Assert.DoesNotContain(blob, b64);
        Assert.Contains("base64 -d > /certs/server.key", b64);

        var digest = "sha256:" + new string('a', 64);
        Assert.Contains(digest, SecretRedactor.Redact($"image {digest}"));
    }
}
