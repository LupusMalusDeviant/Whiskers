using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent.Providers;

/// <summary>Translates an AgentCompletionRequest into the Gemini generateContent body. Gemini uses
/// roles "user"/"model", system_instruction separately, functionDeclarations for tools, and pairs
/// tool responses by FUNCTION NAME (functionResponse), not by call ID. Pure &amp; testable.</summary>
public static class GeminiRequestMapper
{
    public static JsonObject BuildBody(AgentCompletionRequest req)
    {
        var contents = new JsonArray();
        foreach (var m in req.Messages)
            contents.Add(MapContent(m));

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = req.MaxTokens,
                ["temperature"] = req.Temperature,
            },
        };

        if (!string.IsNullOrEmpty(req.System))
            body["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = req.System } }
            };

        if (req.Tools.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var t in req.Tools)
                decls.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.JsonSchema.GetRawText()),
                });
            body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls } };
            body["tool_config"] = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject { ["mode"] = MapMode(req.ToolChoice) }
            };
        }

        return body;
    }

    private static JsonObject MapContent(AgentMessage m)
    {
        switch (m.Role)
        {
            case AgentRole.Tool:
                return new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = m.ToolName ?? "",
                                ["response"] = new JsonObject { ["result"] = m.Text ?? "" },
                            }
                        }
                    }
                };

            case AgentRole.Assistant:
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Text))
                    parts.Add(new JsonObject { ["text"] = m.Text });
                if (m.ToolCalls is { Count: > 0 })
                    foreach (var c in m.ToolCalls)
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = c.Name,
                                ["args"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson),
                            }
                        });
                return new JsonObject { ["role"] = "model", ["parts"] = parts };

            default: // User + System (System ends up as user text, since system_instruction goes separately)
                return new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Text ?? "" } }
                };
        }
    }

    private static string MapMode(AgentToolChoice choice) => choice switch
    {
        AgentToolChoice.None => "NONE",
        AgentToolChoice.Required => "ANY",
        _ => "AUTO",
    };
}

/// <summary>Accumulates the SSE chunks (alt=sse) of a Gemini stream. Text parts are passed through
/// as deltas; functionCall parts arrive complete each and are collected as tool calls.</summary>
public sealed class GeminiStreamAccumulator
{
    private readonly List<AgentToolCall> _calls = new();
    private string? _finish;

    public string? FeedChunk(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return null;

        var candidate = candidates[0];
        if (candidate.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String)
            _finish = fr.GetString();

        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
            return null;

        StringBuilder? text = null;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                (text ??= new StringBuilder()).Append(t.GetString());

            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var args = fc.TryGetProperty("args", out var ar) ? ar.GetRawText() : "{}";
                _calls.Add(new AgentToolCall($"call_{_calls.Count}", name, args));
            }
        }
        return text?.ToString();
    }

    public IReadOnlyList<AgentToolCall> CompletedToolCalls() => _calls;

    public AgentStopReason StopReason()
    {
        if (_calls.Count > 0) return AgentStopReason.ToolCalls;
        return _finish switch
        {
            "MAX_TOKENS" => AgentStopReason.Length,
            "SAFETY" or "RECITATION" => AgentStopReason.Filtered,
            _ => AgentStopReason.Stop,
        };
    }
}
