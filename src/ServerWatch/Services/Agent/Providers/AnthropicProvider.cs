using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Anthropic Messages client (api.anthropic.com/v1/messages) with SSE streaming + tool use.
/// Auth via x-api-key + anthropic-version header. The model default is claude-opus-4-8.</summary>
public sealed class AnthropicProvider : IAgentLlmProvider
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public string Id => "anthropic";

    public AnthropicProvider(HttpClient http, string apiKey, string endpoint)
    {
        _http = http;
        _apiKey = apiKey;
        _endpoint = endpoint;
    }

    public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
        AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = AnthropicRequestMapper.BuildBody(request, stream: true);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, request.Endpoint ?? _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var accumulator = new AnthropicStreamAccumulator();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            // SSE: only the data: lines carry JSON; we ignore event: lines (the type is also in the JSON).
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;

            JsonElement root;
            try { using var doc = JsonDocument.Parse(payload); root = doc.RootElement.Clone(); }
            catch (JsonException) { continue; }

            var text = accumulator.FeedEvent(root);
            if (!string.IsNullOrEmpty(text))
                yield return new AgentStreamDelta(TextDelta: text);
        }

        foreach (var call in accumulator.CompletedToolCalls())
            yield return new AgentStreamDelta(ToolCallDelta: call);

        yield return new AgentStreamDelta(Final: accumulator.StopReason());
    }
}
