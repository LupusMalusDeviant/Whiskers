using System.Text.RegularExpressions;

namespace ServerWatch.Utils;

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

    // password=x, token: x, api_key=x, bearer x, etc.  →  $1=***
    private static readonly Regex KeyedSecret = new(
        @"(?i)(password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|authorization|bearer)\s*[=:]\s*\S+",
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
