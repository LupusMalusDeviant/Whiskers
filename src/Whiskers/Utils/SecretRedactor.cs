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
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    // A large text (task output, up to the executor's 1 MB) gets a budget that scales with it: a timeout withholds
    // the whole text, which must stay the answer to pathological input, not to an ordinary long log.
    private static readonly TimeSpan LargeTimeout = TimeSpan.FromSeconds(2);
    private const int LargeText = 64 * 1024;

    // A value this redactor already wrote ("***" followed by whitespace or the end — "\S+" below always ends there)
    // is left alone, so redacting twice changes nothing. Any other value starting with "***" ("***;x") is a secret
    // like any other. Sinks never run these patterns over JSON text (they redact JSON value by value and shorten it
    // structurally), so a written "***" is never followed by a quote.
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
    private static readonly Pair KeyedSecret = new(
        $@"(?i)({SecretKeys})\s*[=:]\s*{NotRedacted}\S+",
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
        var s = input;
        s = EnvPassword.Replace(s, m => $"{m.Groups[1].Value}=***");
        s = MysqlPwFlag.Replace(s, "-p***");
        s = RedisAuthFlag.Replace(s, "-a ***");
        s = SqlPassword.Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}'***'");
        s = PemPrivateKey.Replace(s, "[REDACTED PRIVATE KEY]");
        s = LongBase64.Replace(s, "[REDACTED BASE64]");
        s = UrlCredentials.Replace(s, m => $"{m.Groups[1].Value}:***@");
        s = JsonSecret.Replace(s, m => $"\"{m.Groups[1].Value}\":\"***\"");
        s = BearerToken.Replace(s, "Bearer ***");
        s = KeyedSecret.Replace(s, m => $"{m.Groups[1].Value}=***");
        return s;
    }
}
