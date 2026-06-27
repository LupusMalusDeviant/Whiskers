using System.Text.Json;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent.Providers;

namespace ServerWatch.Tests;

public class GeminiRequestMapperTests
{
    private static AgentToolDefinition Tool(string name)
    {
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        return new AgentToolDefinition(name, "desc", schema);
    }

    [Fact]
    public void System_goes_to_system_instruction_and_roles_map()
    {
        var req = new AgentCompletionRequest("gemini-2.0-flash", "be brief",
            new[] { new AgentMessage(AgentRole.User, "hi") }, Array.Empty<AgentToolDefinition>());

        var body = GeminiRequestMapper.BuildBody(req);

        Assert.Equal("be brief",
            (string)body["system_instruction"]!["parts"]!.AsArray()[0]!["text"]!);
        Assert.Equal("user", (string)body["contents"]!.AsArray()[0]!["role"]!);
    }

    [Fact]
    public void Tools_become_function_declarations_with_mode()
    {
        var req = new AgentCompletionRequest("m", null,
            new[] { new AgentMessage(AgentRole.User, "go") },
            new[] { Tool("list_containers") }, ToolChoice: AgentToolChoice.Required);

        var body = GeminiRequestMapper.BuildBody(req);

        Assert.Equal("list_containers",
            (string)body["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!["name"]!);
        Assert.Equal("ANY",
            (string)body["tool_config"]!["functionCallingConfig"]!["mode"]!);
    }

    [Fact]
    public void Tool_result_uses_function_name_not_id()
    {
        var toolMsg = new AgentMessage(AgentRole.Tool, "12 containers", ToolCallId: "call_1", ToolName: "list_containers");
        var req = new AgentCompletionRequest("m", null, new[] { toolMsg }, Array.Empty<AgentToolDefinition>());

        var content = GeminiRequestMapper.BuildBody(req)["contents"]!.AsArray()[0]!;
        var fr = content["parts"]!.AsArray()[0]!["functionResponse"]!;
        Assert.Equal("list_containers", (string)fr["name"]!);
        Assert.Equal("12 containers", (string)fr["response"]!["result"]!);
    }

    [Fact]
    public void Assistant_function_call_args_become_object()
    {
        var assistant = new AgentMessage(AgentRole.Assistant, null,
            ToolCalls: new[] { new AgentToolCall("c1", "stop_container", "{\"id\":\"x\"}") });
        var req = new AgentCompletionRequest("m", null, new[] { assistant }, Array.Empty<AgentToolDefinition>());

        var part = GeminiRequestMapper.BuildBody(req)["contents"]!.AsArray()[0]!["parts"]!.AsArray()[0]!;
        Assert.Equal("stop_container", (string)part["functionCall"]!["name"]!);
        Assert.Equal("x", (string)part["functionCall"]!["args"]!["id"]!);
    }
}

public class GeminiStreamAccumulatorTests
{
    private static JsonElement Chunk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Streams_text_and_maps_stop()
    {
        var acc = new GeminiStreamAccumulator();
        Assert.Equal("Hi", acc.FeedChunk(Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi\"}]}}]}")));
        acc.FeedChunk(Chunk("{\"candidates\":[{\"content\":{\"parts\":[]},\"finishReason\":\"STOP\"}]}"));
        Assert.Equal(AgentStopReason.Stop, acc.StopReason());
    }

    [Fact]
    public void Captures_function_call()
    {
        var acc = new GeminiStreamAccumulator();
        acc.FeedChunk(Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"stop_container\",\"args\":{\"id\":\"abc\"}}}]}}]}"));

        var calls = acc.CompletedToolCalls();
        Assert.Single(calls);
        Assert.Equal("stop_container", calls[0].Name);
        Assert.Contains("abc", calls[0].ArgumentsJson);
        Assert.Equal(AgentStopReason.ToolCalls, acc.StopReason());
    }
}

public class GeminiFactoryTests
{
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void Factory_resolves_gemini()
    {
        var provider = new AgentProviderFactory(new StubHttpFactory())
            .Resolve(new ServerWatch.Configuration.AgentSettings { Provider = "gemini" });
        Assert.Equal("gemini", provider.Id);
        Assert.IsType<GeminiProvider>(provider);
    }
}
