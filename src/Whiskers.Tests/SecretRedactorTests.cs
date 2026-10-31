using System.Diagnostics;
using System.Text.RegularExpressions;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>R1-24: the patterns every sink relies on — SQL password syntax, JSON, credentials in URLs, multiline
/// material — and the fail-closed answer when a pattern times out. (<see cref="SecretHygieneTests"/> holds the long
/// list of SQL dialects.)</summary>
public sealed class SecretRedactorTests
{
    private const string Canary = "canary-value-r124";

    [Theory]
    [InlineData("ALTER ROLE app WITH LOGIN PASSWORD 'canary-value-r124' VALID UNTIL 'infinity';")]
    [InlineData("CREATE USER app IDENTIFIED BY \"canary-value-r124\";")]
    [InlineData("ALTER ROLE app PASSWORD $$canary-value-r124$$;")]
    [InlineData("ALTER ROLE app PASSWORD\n  'canary-value-r124';")]
    public void Sql_password_literals_are_redacted_and_the_statement_kept(string sql)
    {
        var redacted = SecretRedactor.Redact(sql);
        Assert.DoesNotContain(Canary, redacted);
        Assert.Contains("'***'", redacted);
        Assert.StartsWith(sql[..10], redacted);
    }

    [Fact]
    public void Json_values_are_redacted_one_by_one_with_escaped_quotes_decoded()
    {
        var json = "{\"sql\":\"ALTER ROLE app PASSWORD 'it\\\"s canary-value-r124'\",\"apiKey\":\"canary-value-r124\",\"serverId\":\"local\"}";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.DoesNotContain(Canary, redacted);
        Assert.Contains("\"serverId\":\"local\"", redacted);
    }

    [Theory]
    [InlineData("https://deploy:canary-value-r124@git.example.org/repo.git")]
    [InlineData("postgres://whiskers:canary-value-r124@db:5432/whiskers")]
    [InlineData("redis://default:canary-value-r124@cache:6379")]
    public void Credentials_in_urls_are_redacted_and_the_host_kept(string url)
    {
        var redacted = SecretRedactor.Redact($"connecting to {url} now");
        Assert.DoesNotContain(Canary, redacted);
        Assert.Contains(":***@", redacted);
    }

    [Fact]
    public void A_token_alone_in_front_of_the_host_is_removed_by_the_sink_rule()
    {
        // A bare token before "@" carries no keyword; the sinks strip URL user info before the patterns.
        Assert.DoesNotContain(Canary, SinkRedaction.Text($"https://{Canary}@github.com/org/repo"));
    }

    [Fact]
    public void Multiline_material_is_redacted_on_every_line()
    {
        var text = string.Join('\n',
            "#!/bin/sh",
            $"export PGPASSWORD={Canary}",
            $"mysql -u root -p{Canary} -e 'select 1'",
            $"redis-cli -a {Canary} ping",
            $"token: {Canary}",
            $"Authorization: Bearer {Canary}",
            new string('Q', 240),
            "echo done");

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(Canary, redacted);
        Assert.Contains("[REDACTED BASE64]", redacted);
        Assert.Contains("echo done", redacted);
        Assert.Equal(8, redacted.Split('\n').Length);
    }

    [Theory]
    [InlineData("password=***;canary-value-r124")]
    [InlineData("PGPASSWORD=***)canary-value-r124 psql")]
    [InlineData("redis-cli -a ***'canary-value-r124 ping")]
    [InlineData("mysql -p***,canary-value-r124 db")]
    public void A_secret_that_starts_with_the_mask_is_still_redacted(string text) =>
        Assert.DoesNotContain(Canary, SecretRedactor.Redact(text));

    [Theory]
    [InlineData("password=x token: y PGPASSWORD=z mysql -pq redis-cli -a r")]
    [InlineData("line\nexport PGPASSWORD=a\nmysql -p'b' -e 'select 1'")]
    public void Redacting_twice_changes_nothing(string text)
    {
        var once = SecretRedactor.Redact(text);
        Assert.Equal(once, SecretRedactor.Redact(once));
    }

    [Fact]
    public void A_long_ordinary_log_is_redacted_not_withheld()
    {
        // Task output runs to the executor's 1 MB: it gets the large-text budget, so only real secrets go.
        var log = string.Join('\n', Enumerable.Range(0, 20_000)
            .Select(i => $"2026-10-31 backup chunk {i} ok, 4096 bytes written to /var/backups/x-{i}.tar"))
            + $"\nexport PGPASSWORD={Canary}\n";

        var redacted = SinkRedaction.Text(log);

        Assert.NotEqual("***", redacted);
        Assert.DoesNotContain(Canary, redacted);
        Assert.Contains("backup chunk 19999 ok", redacted);
    }

    [Fact]
    public void A_pattern_that_times_out_withholds_the_whole_text()
    {
        var result = SecretRedactor.FailClosed($"password={Canary}",
            _ => throw new RegexMatchTimeoutException("x", "y", TimeSpan.FromMilliseconds(100)));

        Assert.Equal("***", result);
    }

    [Fact]
    public void Removing_url_user_info_ends_in_bounded_time_on_hostile_input()
    {
        var input = string.Concat(Enumerable.Repeat("a://", 50_000)) + $"x://{Canary}@host";

        var watch = Stopwatch.StartNew();
        var redacted = SinkRedaction.Text(input);

        Assert.DoesNotContain(Canary, redacted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Pathological_input_ends_in_bounded_time_without_leaking()
    {
        // Thousands of comments between the keyword and the literal, and an unclosed literal: the worst case for the
        // SQL pattern's gap and its mask-to-the-end branch.
        var input = "ALTER ROLE app PASSWORD " + string.Concat(Enumerable.Repeat("/* x */ -- y\n", 20_000))
                    + "'it''s " + Canary + string.Concat(Enumerable.Repeat("''", 20_000));

        var watch = Stopwatch.StartNew();
        var redacted = SecretRedactor.Redact(input);

        Assert.DoesNotContain(Canary, redacted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
