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

    // SQL password literals: PASSWORD 'x', ENCRYPTED PASSWORD $$x$$, SET PASSWORD = 'x', IDENTIFIED BY x,
    // IDENTIFIED WITH plugin BY|AS 'x'  →  keyword '***'. A quoted literal ends at a quote followed by a
    // delimiter, so doubled ('') or backslash-escaped quotes stay inside it; an unclosed one is masked to the end.
    private static readonly Regex SqlPassword = new(
        @"(?is)\b(PASSWORD|IDENTIFIED(?:\s+WITH\s+\S+)?\s+(?:BY|AS))(\s*(?:=\s*|\(\s*)?)" +
        @"(?:'.*?(?:'(?=[\s;,)\\""]|$)|$)|\$(\w*)\$.*?(?:\$\3\$|$)|(?<=(?:BY|AS)\s*)[^\s;'""\\]+)",
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
