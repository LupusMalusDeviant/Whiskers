using System.Collections;
using Microsoft.Extensions.Logging;

namespace Whiskers.Utils;

/// <summary>
/// Stands in front of the logger factory (INV-51, R1-25): every entry reaches the providers already redacted — the
/// formatted message, each structured value, the message template, the scopes and the exception text. A caller that
/// logs a raw value cannot write it out, and an entry that cannot be redacted is withheld whole, never written as it
/// was. The factory behind it keeps the real providers, so their filter rules, scopes and lifetimes are unchanged.
/// </summary>
public sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    /// <summary>What is written instead of an entry whose redaction failed.</summary>
    internal const string WithheldEntry = "<log entry withheld: redaction failed>";

    /// <summary>The factory this one writes to.</summary>
    internal ILoggerFactory Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(Inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => Inner.AddProvider(provider);

    public void Dispose() => Inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        // Redacted before it is pushed: every provider, and the shared scope provider, holds the redacted state only.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(RedactedScope(state));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel)) return;
            Entry entry;
            try
            {
                entry = Entry.Of(state, exception, formatter);
            }
            catch (Exception)
            {
                entry = Entry.Withheld;
            }
            inner.Log(logLevel, eventId, entry, entry.Exception, static (e, _) => e.Message);
        }
    }

    private static object RedactedScope(object state)
    {
        try
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return Text(state.ToString());
            var values = pairs.Select(Redacted).ToList();
            return new Entry(Text(Formatted(values) ?? state.ToString()), values, null);
        }
        catch (Exception)
        {
            return WithheldEntry;
        }
    }

    private static KeyValuePair<string, object?> Redacted(KeyValuePair<string, object?> pair) =>
        new(pair.Key, Value(pair.Value));

    // Numbers, flags, times and ids carry no secret and stay typed; everything else is redacted as text. A list is
    // joined, its pairs (a dictionary) as key=value, so a secret-named key marks its value for the patterns.
    private static object? Value(object? value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        Enum or DateTime or DateTimeOffset or TimeSpan or Guid => value,
        string text => Text(text),
        IEnumerable items => Text(string.Join(", ", items.Cast<object?>().Select(Item))),
        _ => Text(value.ToString()),
    };

    private static string? Item(object? item)
    {
        if (item is DictionaryEntry entry) return $"{entry.Key}={entry.Value}";
        var type = item?.GetType();
        if (type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            return $"{type.GetProperty("Key")!.GetValue(item)}={type.GetProperty("Value")!.GetValue(item)}";
        return item?.ToString();
    }

    /// <summary>The message rebuilt from its template and the redacted values — never from the raw state, whose
    /// formatter would render a value the patterns cannot see (a dictionary as <c>[password, x]</c>). Null when the
    /// template does not match the values.</summary>
    private static string? Formatted(IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var template = values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string;
        if (template is null) return null;
        var args = values.Where(v => v.Key != "{OriginalFormat}").Select(v => v.Value).ToList();
        var sb = new System.Text.StringBuilder(template.Length);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (c != '{')
            {
                sb.Append(c);
                continue;
            }
            var end = template.IndexOf('}', i);
            if (end < 0 || next >= args.Count) return null;
            var hole = template[(i + 1)..end];
            var spec = hole.IndexOfAny([',', ':']) is var at and >= 0 ? hole[at..] : "";
            sb.Append(Render(args[next++], spec));
            i = end;
        }
        return next == args.Count ? sb.ToString() : null;
    }

    private static string Render(object? value, string spec)
    {
        if (value is null) return "(null)";
        if (spec.Length == 0 || value is string) return value.ToString() ?? "";
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0" + spec + "}", value);
        }
        catch (FormatException)
        {
            return value.ToString() ?? "";
        }
    }

    private static string Text(string? text) => SinkRedaction.Text(text) ?? "";

    /// <summary>A redacted entry: the message and the structured values (the template included) a formatter or a
    /// structured provider reads.</summary>
    private sealed class Entry(string message, IReadOnlyList<KeyValuePair<string, object?>> values, Exception? exception)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Entry Withheld = new(WithheldEntry, [], null);

        public string Message { get; } = message;
        public Exception? Exception { get; } = exception;

        public static Entry Of<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(Redacted).ToList()
                : new List<KeyValuePair<string, object?>>();
            return new(Text(Formatted(values) ?? formatter(state, exception)), values,
                exception is null ? null : new RedactedException(exception));
        }

        public KeyValuePair<string, object?> this[int index] => values[index];
        public int Count => values.Count;
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Message;
    }
}

/// <summary>An exception as a log provider sees it: its message and its full text — type, stack trace and inner
/// exceptions — redacted.</summary>
public sealed class RedactedException(Exception original)
    : Exception(SinkRedaction.Text(original.Message))
{
    private readonly string _text = SinkRedaction.Text(original.ToString()) ?? "";

    /// <summary>Part of <see cref="ToString"/>, redacted with it.</summary>
    public override string? StackTrace => null;

    public override string ToString() => _text;
}
