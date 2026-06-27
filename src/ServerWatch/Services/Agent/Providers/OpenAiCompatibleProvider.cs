using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>OpenAI Chat Completions client with streaming + tool calls. Covers openai, openrouter and
/// ollama (their OpenAI-compatible /v1/chat/completions endpoint) — they differ only in
/// base URL and whether a bearer key is required.</summary>
public sealed class OpenAiCompatibleProvider : IAgentLlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public string Id { get; }

    public OpenAiCompatibleProvider(HttpClient http, string id, string apiKey, string endpoint)
    {
        _http = http;
        Id = id;
        _apiKey = apiKey;
        _endpoint = endpoint;
    }

    public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
        AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = OpenAiRequestMapper.BuildBody(request, stream: true);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, request.Endpoint ?? _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var accumulator = new OpenAiStreamAccumulator();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;

            JsonElement root;
            try { using var doc = JsonDocument.Parse(payload); root = doc.RootElement.Clone(); }
            catch (JsonException) { continue; }

            var text = accumulator.FeedChunk(root);
            if (!string.IsNullOrEmpty(text))
                yield return new AgentStreamDelta(TextDelta: text);
        }

        foreach (var call in accumulator.CompletedToolCalls())
            yield return new AgentStreamDelta(ToolCallDelta: call);

        yield return new AgentStreamDelta(Final: accumulator.StopReason());
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(_endpoint));
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    ids.Add(s);
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    /// <summary>Derives the /models URL from the configured chat-completions endpoint.</summary>
    private static string ModelsUrl(string endpoint)
    {
        if (endpoint.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return endpoint.Replace("/chat/completions", "/models", StringComparison.OrdinalIgnoreCase);
        return endpoint.TrimEnd('/') + "/models";
    }
}
