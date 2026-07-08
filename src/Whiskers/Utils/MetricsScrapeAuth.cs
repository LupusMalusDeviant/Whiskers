using System.Security.Cryptography;
using System.Text;

namespace Whiskers.Utils;

/// <summary>Outcome of a <c>/metrics</c> scrape authorization check.</summary>
public enum MetricsScrapeAuthResult
{
    /// <summary>No scrape token is configured — the endpoint is disabled (opt-in); respond 404.</summary>
    Disabled,

    /// <summary>A token is configured but the request's bearer token is missing or wrong; respond 401.</summary>
    Unauthorized,

    /// <summary>The presented bearer token matches — serve the metrics.</summary>
    Ok,
}

/// <summary>
/// Static bearer-token gate for the Prometheus <c>/metrics</c> endpoint, whose payload is the full
/// multi-server container inventory (names, images, per-server resource usage). Kept as a pure function
/// so it can be unit-tested without booting Kestrel.
/// </summary>
public static class MetricsScrapeAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Decides whether a scrape is allowed. With no configured token the endpoint is treated as disabled
    /// (the safe default, so it is never served unauthenticated by accident); otherwise the request must
    /// present <c>Authorization: Bearer &lt;token&gt;</c>, compared in constant time to blunt timing
    /// attacks. The token is neither logged nor echoed — the return value is a plain enum.
    /// </summary>
    public static MetricsScrapeAuthResult Check(string? configuredToken, string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
            return MetricsScrapeAuthResult.Disabled;

        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return MetricsScrapeAuthResult.Unauthorized;

        var presentedBytes = Encoding.UTF8.GetBytes(authorizationHeader[BearerPrefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(configuredToken);

        // FixedTimeEquals returns false (never throws) when the lengths differ, in time independent of
        // where the first mismatching byte is.
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes)
            ? MetricsScrapeAuthResult.Ok
            : MetricsScrapeAuthResult.Unauthorized;
    }
}
