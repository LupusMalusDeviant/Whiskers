using System.Globalization;
using System.Text.Json;

namespace Whiskers.Services.Metrics;

/// <summary>One sample of an instant-vector result: the series' labels (with <c>__name__</c> when the TSDB returns it),
/// its value and the query's sample time. The sample time is when the TSDB evaluated the query — it says nothing about
/// when the exporter collected the value (R1-58).</summary>
public sealed record PromSample(IReadOnlyDictionary<string, string> Labels, double Value, DateTime SampleUtc)
{
    /// <summary>The value of label <paramref name="name"/>, or null.</summary>
    public string? Label(string name) => Labels.TryGetValue(name, out var v) ? v : null;
}

/// <summary>The outcome of one instant query: <see cref="Success"/> with its samples (an empty vector is a success), or a
/// failure with a short reason that never carries the endpoint (it may hold credentials).</summary>
public sealed record PromQueryResult(bool Success, string? Error, IReadOnlyList<PromSample> Samples)
{
    public static PromQueryResult Failed(string error) => new(false, error, []);

    public static PromQueryResult Of(IReadOnlyList<PromSample> samples) => new(true, null, samples);

    /// <summary>
    /// Parses a Prometheus <c>/api/v1/query</c> response body. Only <c>"status": "success"</c> with an instant
    /// <c>vector</c> result counts; anything else — <c>"status": "error"</c>, another result type, a missing or misshaped
    /// field, a value that is no number — is a failure, never an empty (and so harmless-looking) result.
    /// </summary>
    public static PromQueryResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String)
                return Failed("malformed response (no status)");
            if (status.GetString() != "success")
            {
                var type = root.TryGetProperty("errorType", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null;
                return Failed($"query refused ({Short(type) ?? "status " + Short(status.GetString())})");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("resultType", out var resultType) || resultType.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return Failed("malformed response (no data)");
            if (resultType.GetString() != "vector")
                return Failed($"unexpected result type {Short(resultType.GetString())}");

            var samples = new List<PromSample>(result.GetArrayLength());
            foreach (var entry in result.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("metric", out var metric) || metric.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("value", out var pair) || pair.ValueKind != JsonValueKind.Array
                    || pair.GetArrayLength() != 2
                    || pair[0].ValueKind != JsonValueKind.Number || !pair[0].TryGetDouble(out var ts)
                    // Review round 1 (M1): a sample time outside the calendar is malformed, not an overflow.
                    || !(ts >= 0 && ts <= MaxUnixSeconds)
                    || pair[1].ValueKind != JsonValueKind.String
                    || !double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return Failed("malformed response (a sample is misshaped)");

                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var label in metric.EnumerateObject())
                {
                    if (label.Value.ValueKind != JsonValueKind.String) return Failed("malformed response (a label is no string)");
                    labels[label.Name] = label.Value.GetString()!;
                }
                samples.Add(new PromSample(labels, value, FromUnix(ts)));
            }
            return Of(samples);
        }
        catch (JsonException)
        {
            return Failed("malformed response (not JSON)");
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return Failed("malformed response (a timestamp is out of range)");
        }
    }

    /// <summary>The last second <see cref="DateTime"/> can hold (9999-12-31T23:59:59Z).</summary>
    public const double MaxUnixSeconds = 253402300799;

    /// <summary>Unix seconds (with a fraction) as UTC.</summary>
    public static DateTime FromUnix(double seconds) =>
        DateTime.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));

    // A text from the response goes into logs and the UI: bounded, and no control characters.
    private static string? Short(string? text) =>
        text is null ? null : new string(text.Where(c => !char.IsControl(c)).Take(40).ToArray());
}

/// <summary>
/// R1-58: a typed, cancellable Prometheus/VictoriaMetrics instant query (<c>/api/v1/query</c>). Generic on purpose — the
/// first reader that needs labels, the status and a cancellation (disk health) uses it; <see cref="PrometheusMetricsSource"/>
/// keeps its own scalar reads.
/// </summary>
public interface IPrometheusQueryClient
{
    /// <summary>Runs <paramref name="promql"/> against <paramref name="endpoint"/>. Never throws for a failed or malformed
    /// answer (a failed <see cref="PromQueryResult"/>); throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="ct"/> is cancelled. The request is bounded by <see cref="PrometheusQueryClient.Timeout"/>.</summary>
    Task<PromQueryResult> QueryAsync(string endpoint, string promql, CancellationToken ct);
}

/// <inheritdoc cref="IPrometheusQueryClient"/>
public sealed class PrometheusQueryClient(IHttpClientFactory httpFactory, ILogger<PrometheusQueryClient> logger) : IPrometheusQueryClient
{
    /// <summary>The longest one query may take, as for the system-info reads.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Tests only: a shorter bound than <see cref="Timeout"/>.</summary>
    internal TimeSpan? TimeoutForTests { get; init; }

    private TimeSpan Limit => TimeoutForTests ?? Timeout;

    /// <summary>The longest response body read; a disk-health vector is a few kilobytes.</summary>
    internal const int MaxBodyBytes = 4 * 1024 * 1024;

    public async Task<PromQueryResult> QueryAsync(string endpoint, string promql, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint?.TrimEnd('/') + "/api/v1/query", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            return PromQueryResult.Failed("the metrics endpoint is no http(s) URL");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Limit);
        try
        {
            var http = httpFactory.CreateClient(nameof(PrometheusQueryClient));
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri + "?query=" + Uri.EscapeDataString(promql));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            // Prometheus answers a refused query with 400/422 and a JSON body saying why: parsed like any other.
            if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (400 or 422 or 503))
                return PromQueryResult.Failed($"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return PromQueryResult.Failed("response too large");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes) return PromQueryResult.Failed("response too large");
                buffer.Write(chunk, 0, read);
            }
            var parsed = PromQueryResult.Parse(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            return parsed.Success || response.IsSuccessStatusCode
                ? parsed
                : PromQueryResult.Failed($"HTTP {(int)response.StatusCode}: {parsed.Error}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller stopped: not a failed query
        }
        catch (OperationCanceledException)
        {
            return PromQueryResult.Failed($"no answer within {Limit.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException ex)
        {
            // The endpoint is not logged (a URL may carry credentials); the query names what was asked.
            logger.LogDebug("TSDB query {Query} failed: {Error}", promql, ex.HttpRequestError);
            return PromQueryResult.Failed($"the metrics endpoint could not be reached ({ex.HttpRequestError})");
        }
        catch (Exception ex)
        {
            // Review round 1 (M1): fail closed on everything else too — a body cut off mid-stream (HttpIOException, an
            // IOException), a handler's own fault. Only the caller's cancellation is thrown. The message is not kept: it
            // may name the endpoint.
            logger.LogDebug("TSDB query {Query} failed: {Type}", promql, ex.GetType().Name);
            return PromQueryResult.Failed($"the answer could not be read ({ex.GetType().Name})");
        }
    }
}
