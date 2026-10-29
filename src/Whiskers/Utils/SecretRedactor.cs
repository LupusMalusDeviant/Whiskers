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

    // mysql -p<secret>  →  -p***
    private static readonly Regex MysqlPwFlag = new(
        @"-p\S+", RegexOptions.Compiled, Timeout);

    // redis-cli -a <secret>  →  -a ***
    private static readonly Regex RedisAuthFlag = new(
        @"-a\s+\S+", RegexOptions.Compiled, Timeout);

    // PGPASSWORD=x / MYSQL_PWD=x / REDISCLI_AUTH=x  →  NAME=***
    private static readonly Regex EnvPassword = new(
        @"(MYSQL_PWD|PGPASSWORD|REDISCLI_AUTH)=\S+", RegexOptions.Compiled, Timeout);

    // Secret-bearing key names, English and German (UI strings and tool output are partly German:
    // "Temporäres root-Passwort: …" passed straight through before).
    private const string SecretKeys =
        @"password|passwd|pwd|passwort|kennwort|token|secret|api[_-]?key|access[_-]?key|authorization|bearer";

    // password=x, token: x, api_key=x, Passwort: x, etc.  →  $1=***
    private static readonly Regex KeyedSecret = new(
        $@"(?i)({SecretKeys})\s*[=:]\s*\S+",
        RegexOptions.Compiled, Timeout);

    // JSON form: "password": "x"  →  "password":"***"  (the key is followed by a quote, so KeyedSecret misses it)
    private static readonly Regex JsonSecret = new(
        $@"(?i)""([\w-]*(?:{SecretKeys})[\w-]*)""\s*:\s*""(?:[^""\\]|\\.)*""",
        RegexOptions.Compiled, Timeout);

    // Authorization: Bearer <token> without a colon after "Bearer"  →  Bearer ***
    private static readonly Regex BearerToken = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled, Timeout);

    // Credentials in a URL: scheme://user:pass@host  →  scheme://user:***@host
    private static readonly Regex UrlCredentials = new(
        @"(?i)\b([a-z][a-z0-9+.-]*://[^\s:/@]+):[^\s@/]+@",
        RegexOptions.Compiled, Timeout);

    // SQL password literals: PASSWORD 'x', PASSWORD E'x', IDENTIFIED BY "x" (MySQL; after a space, = or ( only, so
    // a JSON key "password": "x" stays for JsonSecret), ENCRYPTED PASSWORD $$x$$, SET PASSWORD = 'x',
    // IDENTIFIED BY x, IDENTIFIED WITH plugin BY|AS 'x'  →  keyword '***'. Doubled ('') and backslash-escaped
    // quotes stay inside the literal (a backslash that is no escape only masks more); an unclosed one is masked to
    // the end. Comments between the tokens (/* … */, -- …, # …) count as space. Meant for decoded text: for JSON,
    // use RedactJson.
    private const string SqlGap = @"(?:\s|/\*.*?(?:\*/|$)|(?:--|\#)[^\n]*(?:\n|$))";
    private static readonly Regex SqlPassword = new(
        $@"(?is)\b(PASSWORD|IDENTIFIED(?:{SqlGap}+WITH{SqlGap}+\S+)?{SqlGap}+(?:BY|AS))({SqlGap}*(?:={SqlGap}*|\({SqlGap}*)?)" +
        $@"(?:(?:[EBNX]|U&)?'(?:[^'\\]|''|\\.)*(?:'|$)|(?<=\s|=|\(|\*/)""(?:[^""\\]|""""|\\.)*(?:""|$)|\$(\w*)\$.*?(?:\$\3\$|$)|(?<=(?:BY|AS){SqlGap}*)[^\s;'""\\]+)",
        RegexOptions.Compiled, Timeout);

    // PEM private keys (certificates are public and stay readable).
    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)",
        RegexOptions.Compiled, Timeout);

    // A long base64 run is almost always an encoded blob — e.g. a key shipped as `echo <b64> | base64 -d`.
    // 200+ characters keeps image digests (64 hex) and ordinary tokens in log lines readable.
    private static readonly Regex LongBase64 = new(
        @"[A-Za-z0-9+/]{200,}={0,2}",
        RegexOptions.Compiled, Timeout);

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

        try
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
        catch (RegexMatchTimeoutException)
        {
            // On pathological input, fail closed: redact the whole string rather than leak it.
            return "***";
        }
    }
}
