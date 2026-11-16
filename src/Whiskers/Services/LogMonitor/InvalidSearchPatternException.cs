using System.Text.RegularExpressions;

namespace Whiskers.Services.LogMonitor;

/// <summary>A log search's regular expression cannot be parsed (review 1.0.0, T3). Its own type, so the page and the
/// <c>search_logs</c> tool report exactly this and nothing else that happens to be an <see cref="ArgumentException"/>.
/// The message names once where and why parsing stopped.</summary>
public sealed class InvalidSearchPatternException(RegexParseException inner)
    : ArgumentException($"The search pattern is not a valid regular expression: {inner.Error} at offset {inner.Offset}.", inner);
