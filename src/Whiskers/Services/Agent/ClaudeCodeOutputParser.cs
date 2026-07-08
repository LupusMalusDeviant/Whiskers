using System.Text;
using System.Text.Json;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;

namespace Whiskers.Services.Agent;

/// <summary>Translates a line of the Claude Code CLI (--output-format stream-json, one JSON object
/// per line) into AgentEvents. Pure &amp; testable. Tool calls ran through our guardrailed MCP, hence
/// the Allow note — the real gate was server-side.</summary>
public static class ClaudeCodeOutputParser
{
    private static readonly GuardrailDecision ViaMcp =
        new(GuardrailVerdict.Allow, "Über die guardrailte Whiskers-MCP ausgeführt.", Array.Empty<string>());

    public static IReadOnlyList<AgentEvent> ParseLine(string? line)
    {
        var events = new List<AgentEvent>();
        if (string.IsNullOrWhiteSpace(line)) return events;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return events; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return events;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "assistant":
                    foreach (var block in ContentBlocks(root))
                    {
                        var bt = block.TryGetProperty("type", out var b) ? b.GetString() : null;
                        if (bt == "text" && block.TryGetProperty("text", out var txt))
                        {
                            var s = txt.GetString();
                            if (!string.IsNullOrEmpty(s)) events.Add(new AgentEvent.AssistantDelta(s));
                        }
                        else if (bt == "tool_use")
                        {
                            var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                            var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                            events.Add(new AgentEvent.ToolProposed(new AgentToolCall(id, name, input), ViaMcp));
                        }
                    }
                    break;

                case "user":
                    foreach (var block in ContentBlocks(root))
                    {
                        if (block.TryGetProperty("type", out var b) && b.GetString() == "tool_result")
                        {
                            var id = block.TryGetProperty("tool_use_id", out var i) ? i.GetString() ?? "" : "";
                            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                            events.Add(new AgentEvent.ToolExecuted(
                                new AgentToolResult(id, ExtractContent(block), isError, ViaMcp)));
                        }
                    }
                    break;

                case "result":
                    var resultError = root.TryGetProperty("is_error", out var re) && re.ValueKind == JsonValueKind.True;
                    if (resultError)
                        events.Add(new AgentEvent.Failed(
                            root.TryGetProperty("result", out var rr) ? rr.GetString() ?? "Claude Code meldete einen Fehler." : "Claude Code meldete einen Fehler."));
                    else
                        events.Add(new AgentEvent.TurnCompleted(AgentStopReason.Stop, new AgentUsage(0, 0)));
                    break;
            }
        }
        return events;
    }

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
            return content.EnumerateArray().ToList();
        return Array.Empty<JsonElement>();
    }

    /// <summary>tool_result.content is a string OR an array of {type:text,text:…} blocks.</summary>
    private static string ExtractContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                    sb.Append(pt.GetString());
            return sb.ToString();
        }
        return content.GetRawText();
    }
}
