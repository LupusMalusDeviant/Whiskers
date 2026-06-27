using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Translates an AgentCompletionRequest into the Anthropic Messages body. Anthropic has
/// system separate (top-level), roles user/assistant (no "tool" role name — tool results are
/// a user turn with tool_result blocks), and tool_use blocks with id/name/input. Deliberately WITHOUT
/// temperature: the 4.x models (claude-opus-4-8 etc.) reject sampling parameters with a 400.</summary>
public static class AnthropicRequestMapper
{
    public static JsonObject BuildBody(AgentCompletionRequest req, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in req.Messages)
        {
            var mapped = MapMessage(m);
            if (mapped != null) messages.Add(mapped);
        }

        var body = new JsonObject
        {
            ["model"] = req.Model,
            ["max_tokens"] = req.MaxTokens,
            ["messages"] = messages,
        };

        if (!string.IsNullOrEmpty(req.System))
            body["system"] = req.System;

        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.JsonSchema.GetRawText()),
                });
            body["tools"] = tools;
            body["tool_choice"] = new JsonObject { ["type"] = MapToolChoice(req.ToolChoice) };
        }

        if (stream) body["stream"] = true;
        return body;
    }

    private static JsonObject? MapMessage(AgentMessage m)
    {
        switch (m.Role)
        {
            case AgentRole.System:
                return null; // System goes through the top-level system field
            case AgentRole.User:
                return new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" };
            case AgentRole.Tool:
                return new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m.ToolCallId ?? "",
                            ["content"] = m.Text ?? "",
                            ["is_error"] = m.IsError,
                        }
                    }
                };
            default: // Assistant
                if (m.ToolCalls is { Count: > 0 })
                {
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Text))
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = c.Id,
                            ["name"] = c.Name,
                            ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson),
                        });
                    return new JsonObject { ["role"] = "assistant", ["content"] = content };
                }
                return new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? "" };
        }
    }

    private static string MapToolChoice(AgentToolChoice choice) => choice switch
    {
        AgentToolChoice.None => "none",
        AgentToolChoice.Required => "any",
        _ => "auto",
    };
}

/// <summary>Accumulates the SSE events of an Anthropic stream: text_delta → text, tool_use blocks are
/// assembled from content_block_start (id/name) + input_json_delta (partial JSON) per content-block index.</summary>
public sealed class AnthropicStreamAccumulator
{
    private sealed class ToolBuf { public string Id = ""; public string Name = ""; public readonly StringBuilder Args = new(); }
    private readonly SortedDictionary<int, ToolBuf> _tools = new();
    private string? _stop;

    public string? FeedEvent(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "content_block_start":
                if (root.TryGetProperty("content_block", out var block)
                    && block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
                {
                    var index = root.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                    var buf = new ToolBuf
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = block.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    };
                    _tools[index] = buf;
                }
                return null;

            case "content_block_delta":
                if (root.TryGetProperty("delta", out var delta))
                {
                    var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    if (dtype == "text_delta" && delta.TryGetProperty("text", out var txt))
                        return txt.GetString();
                    if (dtype == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
                    {
                        var index = root.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                        if (_tools.TryGetValue(index, out var buf))
                            buf.Args.Append(pj.GetString());
                    }
                }
                return null;

            case "message_delta":
                if (root.TryGetProperty("delta", out var md)
                    && md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    _stop = sr.GetString();
                return null;

            default:
                return null;
        }
    }

    public IReadOnlyList<AgentToolCall> CompletedToolCalls() =>
        _tools.Values
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .Select((b, i) => new AgentToolCall(
                string.IsNullOrEmpty(b.Id) ? $"toolu_{i}" : b.Id,
                b.Name,
                b.Args.Length == 0 ? "{}" : b.Args.ToString()))
            .ToList();

    public AgentStopReason StopReason()
    {
        if (_tools.Count > 0) return AgentStopReason.ToolCalls;
        return _stop switch
        {
            "tool_use" => AgentStopReason.ToolCalls,
            "max_tokens" => AgentStopReason.Length,
            "refusal" => AgentStopReason.Filtered,
            _ => AgentStopReason.Stop,
        };
    }
}
