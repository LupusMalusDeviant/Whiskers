using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Utils;

/// <summary>
/// What a value may look like in the application log (CodeQL triage 2026-11): on one line — no value forges a line of
/// its own (<c>cs/log-forging</c>) — and without an account's address (contract 0003, R1-27: no address in a log;
/// <c>cs/exposure-of-sensitive-information</c>). <see cref="RedactingLoggerFactory"/> applies <see cref="Value"/> to
/// every structured value of every entry, so a call site that forgets it cannot write an address or a line break; a
/// call site that logs an account names it with <see cref="Account"/>. The audit rows in the database keep the
/// address — only the application log is affected.
/// </summary>
public static partial class LogSafe
{
    /// <summary>What the log shows for an account that has no name (null or empty).</summary>
    public const string NoAccount = "[account]";

    // Per process and never stored or written: a pseudonym cannot be reversed or recomputed outside this process (no
    // dictionary of addresses helps), yet within one run the same account always gets the same pseudonym, so its log
    // lines still belong together.
    private static readonly byte[] PseudonymKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>A value as one log line: CR, LF, NEL and the Unicode line and paragraph separators become a space.</summary>
    public static string OneLine(string? value) => (value ?? "")
        .Replace("\r", " ").Replace("\n", " ").Replace("\u0085", " ").Replace("\u2028", " ").Replace("\u2029", " ");

    /// <summary>An account (an address, a <c>user:</c> principal id, a local account name) as the log names it: a short
    /// pseudonym, <c>[account:1a2b3c4d]</c>, stable within this process and not reversible. Case does not matter.</summary>
    public static string Account(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return NoAccount;
        var mac = HMACSHA256.HashData(PseudonymKey, Encoding.UTF8.GetBytes(account.Trim().ToLowerInvariant()));
        return "[account:" + Convert.ToHexStringLower(mac, 0, 4) + "]";
    }

    /// <summary>The text with every address in it replaced by its <see cref="Account"/> pseudonym. Only an address
    /// with a domain (<c>name@host.tld</c>) is one: an image digest (<c>nginx@sha256:…</c>) or <c>root@10.0.0.1</c>
    /// stays as it is.</summary>
    public static string Addresses(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return text ?? "";
        try
        {
            return Address().Replace(text, m => Account(m.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: text that cannot be searched for an address is not written.
            return "***";
        }
    }

    /// <summary>A value as the log may show it: <see cref="Addresses"/>, then <see cref="OneLine"/>.</summary>
    public static string Value(string? value) => OneLine(Addresses(value));

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();
}
