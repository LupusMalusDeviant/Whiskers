using System.Diagnostics;
using Microsoft.Extensions.Primitives;
using Whiskers.Utils;

namespace Whiskers.Services.Observability.Correlation;

/// <summary>
/// The id of the HTTP request a piece of work belongs to (R1-53, F-17): taken from a valid <c>X-Request-Id</c> header,
/// else the request's W3C trace id, else a fresh one. It is echoed in the response, carried as the log scope
/// <see cref="ScopeKey"/> and named by the Mutation Gateway next to the operation id, so one id leads from a client's
/// request to the operation, its decision, approval, execution and audit row.
///
/// <para>Like <see cref="Whiskers.Services.Mutations.MutationScope"/> it is ambient (<see cref="AsyncLocal{T}"/>): it
/// flows with the async call chain of the request. A Blazor UI action runs on the circuit's connection, so there the
/// id names the browser connection, not the single click; a background loop has none.</para>
/// </summary>
public static class RequestCorrelation
{
    /// <summary>The request and response header.</summary>
    public const string HeaderName = "X-Request-Id";

    /// <summary>The structured log field (scope and gateway messages) that carries the id.</summary>
    public const string ScopeKey = "HttpRequestId";

    /// <summary>The longest client-supplied id that is accepted.</summary>
    public const int MaxLength = 64;

    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The id of the request this async flow serves, or <c>null</c> outside a request.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>
    /// Whether a client-supplied id may be used and echoed: 1–64 characters of <c>A–Z a–z 0–9 . _ -</c> only, so it can
    /// neither split a header or a log line nor smuggle markup, and the redaction leaves it as it is — an id the
    /// redaction would change (a registered secret, a token shape) is never used, so the id in the log is the id the
    /// client got back, and Whiskers never repeats a secret in a header.
    /// </summary>
    public static bool IsAcceptable(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength) return false;
        foreach (var c in candidate)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        return string.Equals(SinkRedaction.Text(candidate), candidate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The id for a request: the client's <c>X-Request-Id</c> when exactly one acceptable value was sent; otherwise the
    /// trace id of <paramref name="activity"/> (the framework took it from a valid <c>traceparent</c> or made a new
    /// one); otherwise a new random id. Never the raw header when it is not acceptable.
    /// </summary>
    public static string Choose(StringValues clientValues, Activity? activity)
    {
        if (clientValues.Count == 1 && IsAcceptable(clientValues[0])) return clientValues[0]!;
        if (activity is { IdFormat: ActivityIdFormat.W3C } && activity.TraceId != default)
            return activity.TraceId.ToHexString();
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>Makes <paramref name="id"/> the current request id until the returned handle is disposed.</summary>
    public static IDisposable Enter(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var previous = Ambient.Value;
        Ambient.Value = id;
        return new Restore(previous);
    }

    /// <summary>What a log message names when no request is behind the work (a background loop, a start-up step).</summary>
    public static string CurrentOrNone => Current ?? "none";

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
