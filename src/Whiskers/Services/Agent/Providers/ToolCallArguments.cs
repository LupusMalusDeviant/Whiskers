using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whiskers.Services.Agent.Providers;

/// <summary>Reads a tool call's <c>ArgumentsJson</c> back into a request body. A call the model was cut off in
/// (stop reason <c>max_tokens</c>) keeps a truncated JSON fragment in the history; every provider expects an object
/// there, so anything that is not a JSON object becomes <c>{}</c>, like <see cref="AgentArgumentBinder.ParseArguments"/>
/// treats it when the tool runs. Without this, building every later request of the chat throws.</summary>
public static class ToolCallArguments
{
    /// <summary>The arguments as a JSON object; empty, malformed or non-object JSON yields <c>{}</c>.</summary>
    public static JsonObject ToObject(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new JsonObject();
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>The arguments as a JSON object string (the OpenAI wire sends them as a string).</summary>
    public static string ToObjectString(string? argumentsJson) => ToObject(argumentsJson).ToJsonString();
}
