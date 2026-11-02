using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Whiskers.Services.Agent.Providers;

/// <summary>R1-38 (INV-40): how long a provider may take. <see cref="Headers"/> until the response starts,
/// <see cref="Idle"/> between two lines of a streamed answer, <see cref="Total"/> for a whole answer or read. A
/// provider that stops sending is cut off, never waited on: the run ends with a timeout the caller can see.</summary>
public sealed record ProviderTimeouts(TimeSpan Headers, TimeSpan Idle, TimeSpan Total)
{
    public static ProviderTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(10));
}

/// <summary>A provider did not answer within its <see cref="ProviderTimeouts"/>.</summary>
public sealed class ProviderTimeoutException(string message) : TimeoutException(message);

/// <summary>The bounded HTTP steps every provider takes (R1-38). Each step has its own deadline next to the caller's
/// token; a deadline turns into <see cref="ProviderTimeoutException"/>, the caller's cancellation stays an
/// <see cref="OperationCanceledException"/>. The response stays owned (and disposed) by the caller.</summary>
public static class ProviderHttp
{
    /// <summary>Sends and waits for the response headers at most <see cref="ProviderTimeouts.Headers"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request,
        ProviderTimeouts timeouts, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeouts.Headers);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderTimeoutException(
                $"the provider sent no response within {timeouts.Headers.TotalSeconds:0} s.");
        }
    }

    /// <summary>The body of a failed response, read within <see cref="ProviderTimeouts.Idle"/>.</summary>
    public static async Task<string> ErrorTextAsync(HttpResponseMessage response, ProviderTimeouts timeouts,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeouts.Idle);
        try
        {
            return await response.Content.ReadAsStringAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "(the error body did not arrive in time)";
        }
    }

    /// <summary>A JSON body, read within <see cref="ProviderTimeouts.Total"/>.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, ProviderTimeouts timeouts,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeouts.Total);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderTimeoutException(
                $"the provider's answer did not arrive within {timeouts.Total.TotalSeconds:0} s.");
        }
    }

    /// <summary>The lines of a streamed body: each within <see cref="ProviderTimeouts.Idle"/> of the last, all of them
    /// within <see cref="ProviderTimeouts.Total"/>.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(HttpResponseMessage response, ProviderTimeouts timeouts,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(timeouts.Total);
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(total.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Stalled(timeouts, total.IsCancellationRequested);
        }

        await using (stream)
        using (var reader = new StreamReader(stream))
        {
            while (true)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
                {
                    idle.CancelAfter(timeouts.Idle);
                    try
                    {
                        line = await reader.ReadLineAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw Stalled(timeouts, total.IsCancellationRequested);
                    }
                }
                if (line is null) yield break;
                yield return line;
            }
        }
    }

    private static ProviderTimeoutException Stalled(ProviderTimeouts timeouts, bool total) => new(total
        ? $"the provider's answer took longer than {timeouts.Total.TotalSeconds:0} s."
        : $"the provider stopped sending: nothing for {timeouts.Idle.TotalSeconds:0} s.");
}
