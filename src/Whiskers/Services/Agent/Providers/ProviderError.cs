using System.Text.Json;

namespace Whiskers.Services.Agent.Providers;

/// <summary>
/// Shared helper that turns an LLM provider's HTTP error body into a human-readable message, so the
/// agent shows the real reason (e.g. "model not found", "insufficient quota") instead of a bare status
/// code. Used by all agent providers.
/// </summary>
public static class ProviderError
{
    /// <summary>Pulls the human-readable <c>error.message</c> out of a provider error body. Falls back
    /// to the raw (trimmed) body. Never throws. The response body never carries the API key (that lives
    /// in the request headers), so the extracted message cannot leak it.</summary>
    public static string Extract(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(leere Antwort)";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.GetString() is { } m && m.Length > 0)
                return m.Length > 500 ? m[..500] + "…" : m;
        }
        catch (JsonException) { /* not JSON — fall through */ }
        return body.Length > 500 ? body[..500] + "…" : body;
    }
}
