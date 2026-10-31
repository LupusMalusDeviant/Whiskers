namespace Whiskers.Utils;

/// <summary>
/// The secrets Whiskers itself holds for outbound channels (R1-25): bot tokens, webhook URLs, access tokens, SMTP
/// passwords. No pattern can recognize an ntfy topic URL or a custom webhook URL as a secret, so every value is
/// registered when its settings are bound, and <see cref="SecretRedactor"/> replaces each occurrence with
/// <c>***</c> — in log lines, exception texts and every other sink.
/// </summary>
public static class KnownSecrets
{
    /// <summary>Shorter values are no secrets worth matching: replacing them would mangle ordinary text.</summary>
    internal const int MinLength = 8;

    /// <summary>Settings are re-bound on every reload; the oldest values give way so the set cannot grow forever.</summary>
    internal const int MaxCount = 256;

    private static readonly object Gate = new();
    private static readonly List<string> Registered = [];
    private static string[] _longestFirst = [];

    /// <summary>Registers values to withhold from now on; empty and short ones are ignored. A URL is registered as
    /// typed and in its normalized form (lower-case host, default port dropped, as a request or an exception
    /// prints it).</summary>
    public static void Register(params string?[] values)
    {
        lock (Gate)
        {
            var changed = false;
            foreach (var raw in values.SelectMany(Spellings))
            {
                var value = raw?.Trim();
                if (value is null || value.Length < MinLength) continue;
                // A value registered again is current: it moves to the end, so eviction drops the oldest unused one.
                Registered.Remove(value);
                Registered.Add(value);
                changed = true;
            }
            if (!changed) return;
            if (Registered.Count > MaxCount) Registered.RemoveRange(0, Registered.Count - MaxCount);
            // Longest first: a webhook URL is replaced whole before a token inside it is.
            Volatile.Write(ref _longestFirst, Registered.OrderByDescending(v => v.Length).ToArray());
        }
    }

    private static IEnumerable<string?> Spellings(string? value)
    {
        yield return value;
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.AbsoluteUri != value?.Trim())
            yield return uri.AbsoluteUri;
    }

    /// <summary>The text with every registered value replaced by <c>***</c>.</summary>
    internal static string Replace(string text)
    {
        foreach (var value in Volatile.Read(ref _longestFirst))
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, "***", StringComparison.Ordinal);
        return text;
    }
}
