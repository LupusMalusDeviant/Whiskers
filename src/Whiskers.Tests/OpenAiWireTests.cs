using System.Linq;
using System.Text.Json;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class OpenAiRequestMapperTests
{
    private static AgentToolDefinition Tool(string name)
    {
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        return new AgentToolDefinition(name, "desc of " + name, schema);
    }

    [Fact]
    public void Builds_model_messages_and_system()
    {
        var req = new AgentCompletionRequest("gpt-4o-mini", "be helpful",
            new[] { new AgentMessage(AgentRole.User, "hi") }, Array.Empty<AgentToolDefinition>());

        var body = OpenAiRequestMapper.BuildBody(req, stream: true);

        Assert.Equal("gpt-4o-mini", (string)body["model"]!);
        Assert.True((bool)body["stream"]!);
        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", (string)messages[0]!["role"]!);
        Assert.Equal("be helpful", (string)messages[0]!["content"]!);
        Assert.Equal("user", (string)messages[1]!["role"]!);
    }

    [Fact]
    public void Includes_tools_and_tool_choice_when_present()
    {
        var req = new AgentCompletionRequest("m", null,
            new[] { new AgentMessage(AgentRole.User, "go") },
            new[] { Tool("list_containers") }, ToolChoice: AgentToolChoice.Auto);

        var body = OpenAiRequestMapper.BuildBody(req, stream: false);

        Assert.False(body.ContainsKey("stream"));
        Assert.Equal("auto", (string)body["tool_choice"]!);
        var tools = body["tools"]!.AsArray();
        Assert.Equal("function", (string)tools[0]!["type"]!);
        Assert.Equal("list_containers", (string)tools[0]!["function"]!["name"]!);
    }

    [Fact]
    public void Maps_assistant_tool_calls_and_tool_results()
    {
        var assistant = new AgentMessage(AgentRole.Assistant, null,
            ToolCalls: new[] { new AgentToolCall("call_1", "stop_container", "{\"id\":\"x\"}") });
        var toolResult = new AgentMessage(AgentRole.Tool, "stopped", ToolCallId: "call_1");

        var req = new AgentCompletionRequest("m", null, new[] { assistant, toolResult },
            Array.Empty<AgentToolDefinition>());
        var body = OpenAiRequestMapper.BuildBody(req, stream: false);
        var messages = body["messages"]!.AsArray();

        Assert.Equal("assistant", (string)messages[0]!["role"]!);
        Assert.Equal("call_1", (string)messages[0]!["tool_calls"]!.AsArray()[0]!["id"]!);
        Assert.Equal("tool", (string)messages[1]!["role"]!);
        Assert.Equal("call_1", (string)messages[1]!["tool_call_id"]!);
    }
}

public class OpenAiStreamAccumulatorTests
{
    private static JsonElement Chunk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Streams_text_deltas()
    {
        var acc = new OpenAiStreamAccumulator();
        Assert.Equal("Hel", acc.FeedChunk(Chunk("{\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}")));
        Assert.Equal("lo", acc.FeedChunk(Chunk("{\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}")));
        Assert.Equal(AgentStopReason.Stop, acc.StopReason());
        Assert.Empty(acc.CompletedToolCalls());
    }

    [Fact]
    public void Assembles_fragmented_tool_call()
    {
        var acc = new OpenAiStreamAccumulator();
        acc.FeedChunk(Chunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_9\",\"function\":{\"name\":\"stop_container\",\"arguments\":\"{\\\"id\\\":\"}}]}}]}"));
        acc.FeedChunk(Chunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"abc\\\"}\"}}]}}]}"));
        acc.FeedChunk(Chunk("{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}"));

        var calls = acc.CompletedToolCalls();
        Assert.Single(calls);
        Assert.Equal("call_9", calls[0].Id);
        Assert.Equal("stop_container", calls[0].Name);
        Assert.Equal("{\"id\":\"abc\"}", calls[0].ArgumentsJson);
        Assert.Equal(AgentStopReason.ToolCalls, acc.StopReason());
    }
}

public class AgentProviderFactoryTests
{
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static AgentProviderFactory Factory() => new(new StubHttpFactory());

    [Theory]
    [InlineData("openai")]
    [InlineData("openrouter")]
    [InlineData("ollama")]
    public void Resolves_openai_compatible_providers(string id)
    {
        var provider = Factory().Resolve(new Whiskers.Configuration.AgentSettings { Provider = id });
        Assert.Equal(id, provider.Id);
        Assert.IsType<OpenAiCompatibleProvider>(provider);
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            Factory().Resolve(new Whiskers.Configuration.AgentSettings { Provider = "does-not-exist" }));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("http://x", "http://x")]
    public void Empty_endpoint_normalizes_to_null(string? input, string? expected)
    {
        Assert.Equal(expected, AgentProviderFactory.NormalizeEndpoint(input));
    }

    [Fact]
    public void Resolve_with_empty_endpoint_does_not_throw()
    {
        var provider = Factory().Resolve(new Whiskers.Configuration.AgentSettings { Provider = "ollama", Endpoint = "" });
        Assert.Equal("ollama", provider.Id);
    }
}
