using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Translates a provider-neutral AgentCompletionRequest into the OpenAI Chat Completions
/// body (applies 1:1 to openai, openrouter and ollama's OpenAI-compatible endpoint). Pure &amp; testable.</summary>
public static class OpenAiRequestMapper
{
    public static JsonObject BuildBody(AgentCompletionRequest req, bool stream)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrEmpty(req.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = req.System });

        foreach (var m in req.Messages)
            messages.Add(MapMessage(m));

        var body = new JsonObject
        {
            ["model"] = req.Model,
            ["messages"] = messages,
            ["max_tokens"] = req.MaxTokens,
            ["temperature"] = req.Temperature,
        };

        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.JsonSchema.GetRawText()),
                    }
                });
            }
            body["tools"] = tools;
            body["tool_choice"] = MapToolChoice(req.ToolChoice);
        }

        if (stream) body["stream"] = true;
        return body;
    }

    private static JsonObject MapMessage(AgentMessage m)
    {
        switch (m.Role)
        {
            case AgentRole.System:
                return new JsonObject { ["role"] = "system", ["content"] = m.Text ?? "" };
            case AgentRole.User:
                if (!string.IsNullOrEmpty(m.ImageBase64))
                    return new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = m.Text ?? "" },
                            new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:{m.ImageMediaType ?? "image/png"};base64,{m.ImageBase64}",
                                }
                            }
                        }
                    };
                return new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" };
            case AgentRole.Tool:
                return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId ?? "",
                    ["content"] = m.Text ?? "",
                };
            default: // Assistant
                var obj = new JsonObject { ["role"] = "assistant" };
                if (m.ToolCalls is { Count: > 0 })
                {
                    var calls = new JsonArray();
                    foreach (var c in m.ToolCalls)
                        calls.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = c.Name,
                                ["arguments"] = c.ArgumentsJson,
                            }
                        });
                    obj["tool_calls"] = calls;
                    obj["content"] = m.Text;   // darf null sein
                }
                else
                {
                    obj["content"] = m.Text ?? "";
                }
                return obj;
        }
    }

    private static string MapToolChoice(AgentToolChoice choice) => choice switch
    {
        AgentToolChoice.None => "none",
        AgentToolChoice.Required => "required",
        _ => "auto",
    };
}

/// <summary>Accumulates the SSE chunks of an OpenAI stream: text deltas are passed through immediately,
/// fragmented tool_calls (across multiple chunks, indexed) are assembled.</summary>
public sealed class OpenAiStreamAccumulator
{
    private sealed class ToolBuf { public string Id = ""; public string Name = ""; public readonly StringBuilder Args = new(); }
    private readonly SortedDictionary<int, ToolBuf> _tools = new();
    private string? _finish;

    /// <summary>Processes a parsed chunk; returns a text delta if present.</summary>
    public string? FeedChunk(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            _finish = fr.GetString();

        if (!choice.TryGetProperty("delta", out var delta))
            return null;

        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                if (!_tools.TryGetValue(index, out var buf)) { buf = new ToolBuf(); _tools[index] = buf; }
                if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    buf.Id = id.GetString() ?? buf.Id;
                if (tc.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        buf.Name = nm.GetString() ?? buf.Name;
                    if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
                        buf.Args.Append(ar.GetString());
                }
            }
        }

        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString();

        return null;
    }

    public IReadOnlyList<AgentToolCall> CompletedToolCalls() =>
        _tools.Values
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .Select((t, i) => new AgentToolCall(
                string.IsNullOrEmpty(t.Id) ? $"call_{i}" : t.Id,
                t.Name,
                t.Args.Length == 0 ? "{}" : t.Args.ToString()))
            .ToList();

    public AgentStopReason StopReason()
    {
        if (_tools.Count > 0) return AgentStopReason.ToolCalls;
        return _finish switch
        {
            "tool_calls" => AgentStopReason.ToolCalls,
            "length" => AgentStopReason.Length,
            "content_filter" => AgentStopReason.Filtered,
            _ => AgentStopReason.Stop,
        };
    }
}
