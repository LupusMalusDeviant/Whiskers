using System.Text.Json;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class AnthropicRequestMapperTests
{
    private static AgentToolDefinition Tool(string name)
    {
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        return new AgentToolDefinition(name, "desc", schema);
    }

    [Fact]
    public void System_is_top_level_and_no_temperature_sent()
    {
        var req = new AgentCompletionRequest("claude-opus-4-8", "be precise",
            new[] { new AgentMessage(AgentRole.User, "hi") }, Array.Empty<AgentToolDefinition>(),
            Temperature: 0.7);

        var body = AnthropicRequestMapper.BuildBody(req, stream: true);

        Assert.Equal("be precise", (string)body["system"]!);
        Assert.True((bool)body["stream"]!);
        Assert.False(body.ContainsKey("temperature"));   // 4.x lehnt Sampling-Parameter ab
        Assert.Equal("user", (string)body["messages"]!.AsArray()[0]!["role"]!);
    }

    [Fact]
    public void Tools_map_with_input_schema_and_tool_choice_any()
    {
        var req = new AgentCompletionRequest("m", null,
            new[] { new AgentMessage(AgentRole.User, "go") },
            new[] { Tool("list_containers") }, ToolChoice: AgentToolChoice.Required);

        var body = AnthropicRequestMapper.BuildBody(req, stream: false);

        Assert.Equal("list_containers", (string)body["tools"]!.AsArray()[0]!["name"]!);
        Assert.True(body["tools"]!.AsArray()[0]!.AsObject().ContainsKey("input_schema"));
        Assert.Equal("any", (string)body["tool_choice"]!["type"]!);
    }

    [Fact]
    public void Assistant_tool_use_and_tool_result_round_trip()
    {
        var assistant = new AgentMessage(AgentRole.Assistant, null,
            ToolCalls: new[] { new AgentToolCall("toolu_1", "stop_container", "{\"id\":\"x\"}") });
        var toolResult = new AgentMessage(AgentRole.Tool, "stopped", ToolCallId: "toolu_1");

        var req = new AgentCompletionRequest("m", null, new[] { assistant, toolResult },
            Array.Empty<AgentToolDefinition>());
        var messages = AnthropicRequestMapper.BuildBody(req, stream: false)["messages"]!.AsArray();

        var useBlock = messages[0]!["content"]!.AsArray()[0]!;
        Assert.Equal("tool_use", (string)useBlock["type"]!);
        Assert.Equal("toolu_1", (string)useBlock["id"]!);
        Assert.Equal("x", (string)useBlock["input"]!["id"]!);

        var resultBlock = messages[1]!["content"]!.AsArray()[0]!;
        Assert.Equal("user", (string)messages[1]!["role"]!);
        Assert.Equal("tool_result", (string)resultBlock["type"]!);
        Assert.Equal("toolu_1", (string)resultBlock["tool_use_id"]!);
    }
}

public class AnthropicStreamAccumulatorTests
{
    private static JsonElement Ev(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Streams_text_and_maps_end_turn()
    {
        var acc = new AnthropicStreamAccumulator();
        Assert.Equal("Hi", acc.FeedEvent(Ev("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}")));
        acc.FeedEvent(Ev("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}"));
        Assert.Equal(AgentStopReason.Stop, acc.StopReason());
    }

    [Fact]
    public void Assembles_tool_use_from_start_and_input_json_deltas()
    {
        var acc = new AnthropicStreamAccumulator();
        acc.FeedEvent(Ev("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"stop_container\"}}"));
        acc.FeedEvent(Ev("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"id\\\":\"}}"));
        acc.FeedEvent(Ev("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"abc\\\"}\"}}"));
        acc.FeedEvent(Ev("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}"));

        var calls = acc.CompletedToolCalls();
        Assert.Single(calls);
        Assert.Equal("toolu_9", calls[0].Id);
        Assert.Equal("stop_container", calls[0].Name);
        Assert.Equal("{\"id\":\"abc\"}", calls[0].ArgumentsJson);
        Assert.Equal(AgentStopReason.ToolCalls, acc.StopReason());
    }
}

public class AnthropicFactoryTests
{
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void Factory_resolves_anthropic()
    {
        var provider = new AgentProviderFactory(new StubHttpFactory())
            .Resolve(new Whiskers.Configuration.AgentSettings { Provider = "anthropic" });
        Assert.Equal("anthropic", provider.Id);
        Assert.IsType<AnthropicProvider>(provider);
    }
}
