using System.Collections;
using Microsoft.Extensions.Logging;

namespace Whiskers.Utils;

/// <summary>
/// Stands in front of a log provider (INV-51, R1-25): every entry reaches it already redacted — the formatted
/// message, each structured value, the message template, the scopes and the exception text. A caller that logs a
/// raw value cannot write it out, and an entry that cannot be redacted is withheld whole, never written as it was.
/// </summary>
public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    /// <summary>What is written instead of an entry whose redaction failed.</summary>
    internal const string WithheldEntry = "<log entry withheld: redaction failed>";

    /// <summary>The provider this one writes to.</summary>
    internal ILoggerProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(Inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (Inner is ISupportExternalScope scoped) scoped.SetScopeProvider(new RedactingScopes(scopeProvider));
    }

    public void Dispose() => Inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

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

    /// <summary>The scopes an external scope provider hands to the inner provider, each one redacted.</summary>
    private sealed class RedactingScopes(IExternalScopeProvider scopes) : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            scopes.ForEachScope((scope, s) => callback(scope is null ? null : RedactedScope(scope), s), state);

        public IDisposable Push(object? state) => scopes.Push(state);
    }

    private static object RedactedScope(object state)
    {
        try
        {
            return state is IEnumerable<KeyValuePair<string, object?>> values
                ? new Entry(Text(state.ToString()), values.Select(Redacted).ToList(), null)
                : Text(state.ToString());
        }
        catch (Exception)
        {
            return WithheldEntry;
        }
    }

    private static KeyValuePair<string, object?> Redacted(KeyValuePair<string, object?> pair) =>
        new(pair.Key, Value(pair.Value));

    // Numbers, flags, times and ids carry no secret and stay typed; everything else is redacted as text.
    private static object? Value(object? value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        Enum or DateTime or DateTimeOffset or TimeSpan or Guid => value,
        _ => Text(value is IEnumerable and not string ? string.Join(", ", ((IEnumerable)value).Cast<object?>()) : value.ToString()),
    };

    private static string Text(string? text) => SinkRedaction.Text(text) ?? "";

    /// <summary>A redacted entry: the message and the structured values (the template included) a formatter or a
    /// structured provider reads.</summary>
    private sealed class Entry(string message, IReadOnlyList<KeyValuePair<string, object?>> values, Exception? exception)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Entry Withheld = new(WithheldEntry, [], null);

        public string Message { get; } = message;
        public Exception? Exception { get; } = exception;

        public static Entry Of<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            new(Text(formatter(state, exception)),
                state is IEnumerable<KeyValuePair<string, object?>> values ? values.Select(Redacted).ToList() : [],
                exception is null ? null : new RedactedException(exception));

        public KeyValuePair<string, object?> this[int index] => values[index];
        public int Count => values.Count;
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Message;
    }
}

/// <summary>An exception as a log provider may see it: its type name, and its message and full text (stack trace and
/// inner exceptions included) redacted.</summary>
public sealed class RedactedException(Exception original)
    : Exception(SinkRedaction.Text(original.Message))
{
    private readonly string _text = SinkRedaction.Text(original.ToString()) ?? "";

    /// <summary>The type of the exception that was logged.</summary>
    public string OriginalType { get; } = original.GetType().FullName ?? original.GetType().Name;

    /// <summary>Part of <see cref="ToString"/>, redacted with it.</summary>
    public override string? StackTrace => null;

    public override string ToString() => _text;
}
