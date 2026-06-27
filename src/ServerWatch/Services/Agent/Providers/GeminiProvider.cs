using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Google Gemini client (generateContent) with SSE streaming + function calling. The model is
/// in the URL; the API key goes as an x-goog-api-key header (not in the URL, so it does not end up in logs).</summary>
public sealed class GeminiProvider : IAgentLlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseEndpoint;

    public string Id => "gemini";

    public GeminiProvider(HttpClient http, string apiKey, string baseEndpoint)
    {
        _http = http;
        _apiKey = apiKey;
        _baseEndpoint = baseEndpoint.TrimEnd('/');
    }

    public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
        AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = (request.Endpoint ?? _baseEndpoint).TrimEnd('/');
        var url = $"{baseUrl}/{request.Model}:streamGenerateContent?alt=sse";
        var body = GeminiRequestMapper.BuildBody(request);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
            httpReq.Headers.Add("x-goog-api-key", _apiKey);

        using var response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var accumulator = new GeminiStreamAccumulator();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();

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
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseEndpoint);
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Add("x-goog-api-key", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var m in models.EnumerateArray())
            {
                // Keep only models that can actually generate content.
                var canGenerate = m.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.ValueKind == JsonValueKind.Array
                    && methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (!canGenerate) continue;

                if (m.TryGetProperty("name", out var name) && name.GetString() is { } s)
                    ids.Add(s.StartsWith("models/", StringComparison.Ordinal) ? s["models/".Length..] : s);
            }
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }
}
