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

    // ---- Contract 0017 C2: the whole Authorization value, whatever the scheme --------------------------------------

    [Fact]
    public void A_basic_authorization_header_is_redacted()
    {
        var credential = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("deploy:example-value-1"));
        var redacted = SecretRedactor.Redact($"curl -H 'Authorization: Basic {credential}' https://registry.example/v2/");
        Assert.False(redacted.Contains(credential.TrimEnd('='), StringComparison.Ordinal), "the credential survives redaction: " + redacted);
    }

    [Theory]
    [InlineData("curl -H 'Authorization: Basic ZGVwbG95OmNhbmFyeS12YWx1ZS1yMTI0' https://r.example/v2/",
        "curl -H 'Authorization: Basic ***' https://r.example/v2/")]
    [InlineData("curl -H \"Authorization: Bearer canary-value-r124\" https://r.example/",
        "curl -H \"Authorization: Bearer ***\" https://r.example/")]
    [InlineData("Authorization: Digest username=\"u\", response=\"canary-value-r124\"", "Authorization: Digest ***")]
    [InlineData("Authorization: Negotiate canary-value-r124", "Authorization: Negotiate ***")]
    [InlineData("Authorization=Basic canary-value-r124", "Authorization=Basic ***")]
    [InlineData("Authorization: NTLM TlRMTVNTUAABAAAAB4IIog==", "Authorization: NTLM ***")]
    // A scheme outside the list is no scheme: the whole value goes.
    [InlineData("HTTP_AUTHORIZATION=Token canary-value-r124", "HTTP_AUTHORIZATION=***")]
    [InlineData("Proxy-Authorization: Basic canary-value-r124\nnext line", "Proxy-Authorization: Basic ***\nnext line")]
    [InlineData("Proxy-Authorization: canary-value-r124", "Proxy-Authorization: ***")]
    // A one-word value is removed whole, also when it is a scheme word with nothing after it.
    [InlineData("authorization: canary-value-r124", "authorization: ***")]
    [InlineData("Authorization: ghp_canaryvaluer124canaryvaluer124", "Authorization: ***")]
    [InlineData("curl -H 'Authorization: ghp_canaryvaluer124' https://api.example/", "curl -H 'Authorization: ***' https://api.example/")]
    [InlineData("Authorization: Basic", "Authorization: ***")]
    // A backslash does not end the value (review of 37f88726, M1).
    [InlineData(@"Authorization: DOMAIN\user:hunter2secret", "Authorization: ***")]
    [InlineData(@"Authorization: \hunter2secret", "Authorization: ***")]
    [InlineData(@"HTTP_AUTHORIZATION=tok\en-hunter2secret", "HTTP_AUTHORIZATION=***")]
    [InlineData(@"-H Authorization:\ Bearer\ hunter2secret", "-H Authorization:***")]
    [InlineData(@"curl -H ""Authorization: Digest username=\""u\"", response=\""hunter2secret\"""" https://x",
        @"curl -H ""Authorization: Digest ***"" https://x")]
    // A header inside an escaped JSON string ends at the escaped quote; the document stays intact.
    [InlineData(@"{""cmd"":""curl -H \""Authorization: Basic hunter2secret\"" https://x"",""n"":""kept""}",
        @"{""cmd"":""curl -H \""Authorization: Basic ***\"" https://x"",""n"":""kept""}")]
    // The same with the quotes written as backslash-u0022 (the default JSON encoder), built from pieces.
    [InlineData(@"{""cmd"":""curl -H \" + "u0022Authorization: Basic hunter2secret" + @"\" + @"u0022 https://x"",""n"":""kept""}",
        @"{""cmd"":""curl -H \" + "u0022Authorization: Basic ***" + @"\" + @"u0022 https://x"",""n"":""kept""}")]
    // "***" counts as already redacted only when the value ends there (m2).
    [InlineData("Authorization: ***hunter2secret", "Authorization: ***")]
    [InlineData("Authorization: Bearer ***hunter2secret", "Authorization: Bearer ***")]
    // A prefixed header inside a quoted argument keeps what follows the argument (m3).
    [InlineData("curl -H 'Proxy-Authorization: Basic hunter2secret' -H 'X: y' https://x",
        "curl -H 'Proxy-Authorization: Basic ***' -H 'X: y' https://x")]
    // The credential on the next line after the scheme, and a single-quoted key.
    [InlineData("Authorization: Basic \nhunter2secret", "Authorization: Basic ***")]
    [InlineData("{'Authorization': 'Basic hunter2secret'}", "{'Authorization': '***'}")]
    public void An_authorization_value_is_redacted_down_to_its_scheme(string input, string expected)
    {
        var redacted = SecretRedactor.Redact(input);
        Assert.Equal(expected, redacted);
        Assert.Equal(redacted, SecretRedactor.Redact(redacted));   // idempotent
    }

    [Fact]
    public void A_logged_authorization_header_in_a_json_document_keeps_the_fields_after_it()
    {
        const string json = """{"log":"> Authorization: Basic hunter2secret","next":"kept"}""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.Equal("""{"log":"> Authorization: Basic ***","next":"kept"}""", redacted);
        Assert.Equal(redacted, SecretRedactor.RedactJson(redacted));
    }

    /// <summary>The Authorization rule stays linear on pathological input (the 1 s budget would withhold the text).</summary>
    [Theory]
    [InlineData("repeat")]
    [InlineData("quote-spaces")]
    [InlineData("newlines")]
    [InlineData("backslashes")]
    [InlineData("escaped-quotes")]
    public void The_authorization_rule_has_no_catastrophic_backtracking(string shape)
    {
        var input = shape switch
        {
            "repeat" => string.Concat(Enumerable.Repeat("authorization: ", 20000)),
            "quote-spaces" => string.Concat(Enumerable.Repeat("\"authorization" + new string(' ', 50), 5000)),
            "newlines" => string.Concat(Enumerable.Repeat("authorization:" + new string('\n', 300), 1000)) + "***",
            "backslashes" => "\"authorization: " + new string('\\', 300000),
            _ => string.Concat(Enumerable.Repeat("\\\"authorization: Basic \\x", 20000)),
        };

        var watch = Stopwatch.StartNew();
        var redacted = SecretRedactor.Redact(input);
        watch.Stop();

        Assert.NotEqual("***", redacted);   // not withheld by a timeout
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{shape}: {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void An_authorization_header_in_json_keeps_the_document_valid()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            command = $"curl -H \"Authorization: Basic {Canary}\" https://r.example/",
            next = "kept",
        });

        var redacted = SecretRedactor.RedactJson(json);

        Assert.DoesNotContain(Canary, redacted);
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal("kept", document.RootElement.GetProperty("next").GetString());
        Assert.Equal(redacted, SecretRedactor.RedactJson(redacted));
        // A raw pass over the escaped document (no decoding) keeps it valid as well.
        using var raw = System.Text.Json.JsonDocument.Parse(SecretRedactor.Redact(json));
        Assert.Equal("kept", raw.RootElement.GetProperty("next").GetString());
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
