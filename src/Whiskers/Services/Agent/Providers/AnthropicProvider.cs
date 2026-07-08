using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent.Providers;

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
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Anthropic {(int)response.StatusCode}: {ProviderError.Extract(await response.Content.ReadAsStringAsync(ct))}");

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

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var url = _endpoint.Contains("/messages", StringComparison.OrdinalIgnoreCase)
            ? _endpoint.Replace("/messages", "/models", StringComparison.OrdinalIgnoreCase)
            : _endpoint.TrimEnd('/') + "/models";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Anthropic {(int)resp.StatusCode}: {ProviderError.Extract(await resp.Content.ReadAsStringAsync(ct))}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    ids.Add(s);
        return ids;
    }
}
