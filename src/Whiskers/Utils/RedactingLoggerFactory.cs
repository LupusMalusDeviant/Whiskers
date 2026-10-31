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
            var raw = pairs.ToList();
            var values = raw.Select(Redacted).ToList();
            return new Entry(Text(Message(raw, values, () => state.ToString())), values, null);
        }
        catch (Exception)
        {
            return WithheldEntry;
        }
    }

    // A value logged under a secret's name ({Password}, {ApiKey}) is *** whatever it looks like: a structured provider
    // writes it next to its name, where no pattern would see a bare value as a secret. Numbers, flags, times and ids
    // stay typed ({InputTokens} is a count).
    private static KeyValuePair<string, object?> Redacted(KeyValuePair<string, object?> pair) =>
        new(pair.Key, Value(pair.Value) is var value && value is string && pair.Key != "{OriginalFormat}"
                      && SinkRedaction.IsSecretName(pair.Key)
            ? "***"
            : value);

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

    /// <summary>The message of an entry. With a template it is rebuilt from the raw template's holes and the redacted
    /// values — never formatted from the raw state, whose formatter would render a value the patterns cannot see (a
    /// dictionary as <c>[password, x]</c>); a template whose holes do not match the values is withheld. Only a state
    /// without a template is formatted by its own formatter (and redacted after it, like every message).</summary>
    private static string? Message(IReadOnlyList<KeyValuePair<string, object?>> raw,
                                   IReadOnlyList<KeyValuePair<string, object?>> redacted, Func<string?> formatter)
    {
        if (raw.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is not string template) return formatter();
        var args = redacted.Where(v => v.Key != "{OriginalFormat}").ToList();
        // Without arguments the template is the message as it stands, braces and all (as the framework writes it).
        return args.Count == 0 ? template : Formatted(template, args) ?? WithheldEntry;
    }

    /// <summary>The template with each hole filled; null when it has more holes than values or an open brace.</summary>
    /// <remarks>A hole takes the value of its own name (a source-generated state lists its values in parameter order,
    /// not in template order), else the next unused one.</remarks>
    private static string? Formatted(string template, IReadOnlyList<KeyValuePair<string, object?>> args)
    {
        var used = new bool[args.Count];
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
            if (end < 0) return null;
            var hole = template[(i + 1)..end];
            var spec = hole.IndexOfAny([',', ':']) is var at and >= 0 ? hole[at..] : "";
            var name = spec.Length > 0 ? hole[..^spec.Length] : hole;
            var slot = -1;
            for (var k = 0; k < args.Count && slot < 0; k++)
                if (!used[k] && args[k].Key == name) slot = k;
            while (slot < 0 && next < args.Count)
                if (!used[next++]) slot = next - 1;
            if (slot < 0) return null;
            used[slot] = true;
            sb.Append(Render(args[slot].Value, spec));
            i = end;
        }
        return sb.ToString();
    }

    private static string Render(object? value, string spec)
    {
        if (value is null) return "(null)";
        if (spec.Length == 0) return value.ToString() ?? "";
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
            var raw = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToList()
                : new List<KeyValuePair<string, object?>>();
            var values = raw.Select(Redacted).ToList();
            return new(Text(Message(raw, values, () => formatter(state, exception))), values,
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
