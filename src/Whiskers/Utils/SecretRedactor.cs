using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Whiskers.Utils;

/// <summary>
/// Masks common secrets in a command/log string before it is persisted (e.g. to the audit log).
/// The actual executed command is never altered — only the string used for display/storage.
/// </summary>
public static class SecretRedactor
{
    // The budget guards against catastrophic backtracking, it is no speed limit: a timeout withholds the whole text,
    // so it must not fire on ordinary input on a loaded host (thread preemption, the first run's JIT of a compiled
    // pattern) — 100 ms did, under parallel load, and turned short values into "***".
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // A large text (task output, up to the executor's 1 MB) gets a budget that scales with it: a timeout withholds
    // the whole text, which must stay the answer to pathological input, not to an ordinary long log.
    private static readonly TimeSpan LargeTimeout = TimeSpan.FromSeconds(5);
    private const int LargeText = 64 * 1024;

    // A value this redactor already wrote ("***" followed by whitespace or the end — "\S+" below always ends there)
    // is left alone, so redacting twice changes nothing. Any other value starting with "***" ("***;x") is a secret
    // like any other. Sinks redact JSON value by value and shorten it structurally, so a written "***" is never
    // followed by a quote there; text that is not valid JSON (a cut tool output) can lose what follows a redacted
    // value, never keep a secret.
    private const string NotRedacted = @"(?!\*\*\*(?:\s|$))";

    // mysql -p<secret>, "-p<secret>", '-p<secret>'  →  -p***  — a flag, so not inside a word: "web-prod" is a name.
    private static readonly Pair MysqlPwFlag = new(
        $@"(?<![\w-])-p{NotRedacted}\S+", RegexOptions.Compiled);

    // redis-cli -a <secret>  →  -a ***  — a flag, so not inside a word: "data-a daily" is a name.
    private static readonly Pair RedisAuthFlag = new(
        $@"(?<![\w-])-a\s+{NotRedacted}\S+", RegexOptions.Compiled);

    // PGPASSWORD=x / MYSQL_PWD=x / REDISCLI_AUTH=x  →  NAME=***
    private static readonly Pair EnvPassword = new(
        $@"(MYSQL_PWD|PGPASSWORD|REDISCLI_AUTH)={NotRedacted}\S+", RegexOptions.Compiled);

    // Secret-bearing key names, English and German (UI strings and tool output are partly German:
    // "Temporäres root-Passwort: …" passed straight through before).
    private const string SecretKeys =
        @"password|passwd|pwd|passwort|kennwort|token|secret|api[_-]?key|access[_-]?key|authorization|bearer";

    // password=x, token: x, api_key=x, Passwort: x, etc.  →  $1=***
    // "authorization" has its own rule (AuthorizationValue), which keeps the scheme; here it would cut the value
    // after the scheme word.
    private static readonly Pair KeyedSecret = new(
        $@"(?i)((?!authorization)(?:{SecretKeys}))\s*[=:]\s*{NotRedacted}\S+",
        RegexOptions.Compiled);

    // INV-51 (contract 0017 C2): "Authorization: <scheme> <credentials>" and "Authorization=<…>" (Proxy-Authorization
    // and HTTP_AUTHORIZATION included) lose the whole value. The scheme word stays readable only when it is one of
    // Basic, Bearer, Digest, Negotiate, NTLM and more text follows it ("Authorization: Basic ***"); any other value,
    // a one-word token included, goes whole ("Authorization: ***"). Where the value ends:
    //  - the header opens a quoted argument ('Authorization: …', "Proxy-Authorization: …"): at the closing quote; an
    //    escaped quote inside (\") belongs to the value (Digest parameters);
    //  - the quote that opens it is itself escaped (\"Authorization: …\" inside a JSON string or a quoted command):
    //    at the matching escaped quote;
    //  - otherwise: at the end of the line, or before an escaped quote (\" \', or backslash-u0022 / -u0027) of a JSON string that
    //    holds the header. A backslash alone never ends it (DOMAIN\user:password).
    // A value this rule already wrote ("***" followed by the end, whitespace or a closing quote) is left alone, so
    // applying it twice, or to the re-serialized JSON of values it redacted, changes nothing; "***secret" is a value
    // like any other. Ending at the line end masks more than the value when the header is not quoted: the safe
    // direction. A scheme with its credential on the next line loses that line too.
    private static readonly Pair AuthorizationValue = new(
        // The separator and the scheme are atomic: giving back a space or the scheme word must not let a value this
        // rule wrote ("Basic ***") match again as a new value.
        @"(?i)(?:(?<esc>\\)?(?<q>['""])(?<pre>[\w-]*?))?(?<key>authorization)" +
        @"(?>(?<sep>[ \t]*[=:]\s*)(?:(?<scheme>Basic|Bearer|Digest|Negotiate|NTLM)\s+)?)" +
        @"(?!\*\*\*(?:\s|$|\\?['""]|\\u00(?:22|27)))" +
        @"(?(q)(?(esc)(?:(?!\\\k<q>)[^\r\n])+|(?:\\[^\r\n]|(?!\k<q>)[^\r\n\\])+)" +
        @"|(?:(?!\\['""]|\\u00(?:22|27))[^\r\n])+)",
        RegexOptions.Compiled);

    // A single-quoted key, as a Python dict or a log prints headers: 'Authorization': 'Basic x'  →  'Authorization': '***'
    // (the double-quoted JSON form is JsonSecret's).
    private static readonly Pair QuotedAuthorizationKey = new(
        @"(?i)(?<kq>')(?<key>[\w-]*authorization)\k<kq>(?<sep>\s*[=:]\s*)(?<vq>['""])(?:\\.|(?!\k<vq>)[^\\\r\n])*\k<vq>",
        RegexOptions.Compiled);

    // JSON form: "password": "x"  →  "password":"***"  (the key is followed by a quote, so KeyedSecret misses it)
    private static readonly Pair JsonSecret = new(
        $@"(?i)""([\w-]*(?:{SecretKeys})[\w-]*)""\s*:\s*""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled);

    // Authorization: Bearer <token> without a colon after "Bearer"  →  Bearer ***
    private static readonly Pair BearerToken = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled);

    // Credentials in a URL: scheme://user:pass@host  →  scheme://user:***@host
    private static readonly Pair UrlCredentials = new(
        @"(?i)\b([a-z][a-z0-9+.-]*://[^\s:/@]+):[^\s@/]+@",
        RegexOptions.Compiled);

    // SQL password literals: PASSWORD 'x', PASSWORD E'x', IDENTIFIED BY "x" (MySQL; after a space, = or ( only, so
    // a JSON key "password": "x" stays for JsonSecret), ENCRYPTED PASSWORD $$x$$, SET PASSWORD = 'x',
    // IDENTIFIED BY x, IDENTIFIED WITH plugin BY|AS 'x'  →  keyword '***'. Doubled ('') and backslash-escaped
    // quotes stay inside the literal (a backslash that is no escape only masks more); an unclosed one is masked to
    // the end. Comments between the tokens (/* … */, -- …, # …) count as space. Meant for decoded text: for JSON,
    // use RedactJson.
    private const string SqlGap = @"(?:\s|/\*.*?(?:\*/|$)|(?:--|\#)[^\n]*(?:\n|$))";
    private static readonly Pair SqlPassword = new(
        $@"(?is)\b(PASSWORD|IDENTIFIED(?:{SqlGap}+WITH{SqlGap}+\S+)?{SqlGap}+(?:BY|AS))({SqlGap}*(?:={SqlGap}*|\({SqlGap}*)?)" +
        $@"(?:(?:[EBNX]|U&)?'(?:[^'\\]|''|\\.)*(?:'|$)|(?<=\s|=|\(|\*/)""(?:[^""\\]|""""|\\.)*(?:""|$)|\$(\w*)\$.*?(?:\$\3\$|$)|(?<=(?:BY|AS){SqlGap}*)[^\s;'""\\]+)",
        RegexOptions.Compiled);

    // PEM private keys (certificates are public and stay readable).
    private static readonly Pair PemPrivateKey = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)",
        RegexOptions.Compiled);

    // A long base64 run is almost always an encoded blob — e.g. a key shipped as `echo <b64> | base64 -d`.
    // 200+ characters keeps image digests (64 hex) and ordinary tokens in log lines readable.
    private static readonly Pair LongBase64 = new(
        @"[A-Za-z0-9+/]{200,}={0,2}",
        RegexOptions.Compiled);

    // Capability URLs: the path itself is the secret (R1-25). A Telegram bot token (/bot<id>:<token>/), a Discord
    // webhook token (/api/webhooks/<id>/<token>), a Slack webhook (hooks.slack.com/services/…) and a Mattermost or
    // other incoming-webhook key (/hooks/<key>)  →  the secret part becomes ***.
    private static readonly Pair TelegramBotToken = new(
        @"(?i)(/bot)\d{5,}:[A-Za-z0-9_-]{20,}", RegexOptions.Compiled);
    private static readonly Pair DiscordWebhook = new(
        @"(?i)(/api/webhooks/\d+/)[A-Za-z0-9_-]{20,}", RegexOptions.Compiled);
    private static readonly Pair SlackWebhook = new(
        @"(?i)(hooks\.slack\.com/services/)[A-Za-z0-9_/-]{8,}", RegexOptions.Compiled);
    private static readonly Pair HookKey = new(
        @"(?i)(/hooks/)[A-Za-z0-9]{20,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Relaxed =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Redacts a JSON document: every string value on its own, decoded (so an escaped quote is a quote again), then
    /// the re-written text for keyed secrets ("password": "…"). Text that is no JSON is redacted as it is.
    /// </summary>
    public static string RedactJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return json ?? "";
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return Redact(json); }
        if (node is null)
            return Redact(json);
        RedactValues(node);
        return Redact(node.ToJsonString(Relaxed));
    }

    private static void RedactValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                    if (obj[key] is { } child)
                    {
                        if (Redacted(child) is { } text) obj[key] = text;
                        else RedactValues(child);
                    }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } child)
                    {
                        if (Redacted(child) is { } text) arr[i] = text;
                        else RedactValues(child);
                    }
                break;
        }
    }

    private static string? Redacted(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? Redact(text) : null;

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? "";
        return FailClosed(input, Patterns);
    }

    /// <summary>Runs <paramref name="redact"/>; when a pattern times out on pathological input the whole string is
    /// withheld rather than leaked.</summary>
    internal static string FailClosed(string input, Func<string, string> redact)
    {
        try
        {
            return redact(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return "***";
        }
    }

    /// <summary>One pattern, compiled with the normal and with the large-text budget.</summary>
    private sealed class Pair(string pattern, RegexOptions options = RegexOptions.None)
    {
        private readonly Regex _normal = new(pattern, options | RegexOptions.Compiled, Timeout);
        private readonly Regex _large = new(pattern, options | RegexOptions.Compiled, LargeTimeout);

        public string Replace(string input, string replacement) => For(input).Replace(input, replacement);
        public string Replace(string input, MatchEvaluator evaluator) => For(input).Replace(input, evaluator);
        private Regex For(string input) => input.Length > LargeText ? _large : _normal;
    }

    private static string Patterns(string input)
    {
        var s = KnownSecrets.Replace(input);
        s = EnvPassword.Replace(s, m => $"{m.Groups[1].Value}=***");
        s = MysqlPwFlag.Replace(s, "-p***");
        s = RedisAuthFlag.Replace(s, "-a ***");
        s = SqlPassword.Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}'***'");
        s = PemPrivateKey.Replace(s, "[REDACTED PRIVATE KEY]");
        s = LongBase64.Replace(s, "[REDACTED BASE64]");
        s = UrlCredentials.Replace(s, m => $"{m.Groups[1].Value}:***@");
        s = TelegramBotToken.Replace(s, m => $"{m.Groups[1].Value}***");
        s = DiscordWebhook.Replace(s, m => $"{m.Groups[1].Value}***");
        s = SlackWebhook.Replace(s, m => $"{m.Groups[1].Value}***");
        s = HookKey.Replace(s, m => $"{m.Groups[1].Value}***");
        s = JsonSecret.Replace(s, m => $"\"{m.Groups[1].Value}\":\"***\"");
        s = QuotedAuthorizationKey.Replace(s, m =>
            $"{m.Groups["kq"].Value}{m.Groups["key"].Value}{m.Groups["kq"].Value}{m.Groups["sep"].Value}" +
            $"{m.Groups["vq"].Value}***{m.Groups["vq"].Value}");
        s = AuthorizationValue.Replace(s, m =>
            $"{m.Groups["esc"].Value}{m.Groups["q"].Value}{m.Groups["pre"].Value}{m.Groups["key"].Value}{m.Groups["sep"].Value}" +
            (m.Groups["scheme"].Success ? m.Groups["scheme"].Value + " " : "") + "***");
        s = BearerToken.Replace(s, "Bearer ***");
        s = KeyedSecret.Replace(s, m => $"{m.Groups[1].Value}=***");
        return s;
    }
}
