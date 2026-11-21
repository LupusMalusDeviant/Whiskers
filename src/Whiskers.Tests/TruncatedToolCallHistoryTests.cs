using System.Text.Json;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

/// <summary>A tool call the model was cut off in (stop reason <c>max_tokens</c>) keeps a truncated JSON fragment as its
/// <c>ArgumentsJson</c> in the session history. Every request mapper reads it back as an object (<c>{}</c> when it is
/// not one), so the next request of the chat still builds.</summary>
public sealed class TruncatedToolCallHistoryTests
{
    [Fact]
    public void A_tool_call_cut_off_by_the_token_limit_does_not_break_the_next_request()
    {
        var stream = new AnthropicStreamAccumulator();
        void Feed(string json)
        {
            using var doc = JsonDocument.Parse(json);
            stream.FeedEvent(doc.RootElement);
        }
        Feed("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"list_containers"}}""");
        Feed("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"serverId\": \"bad"}}""");
        Feed("""{"type":"message_delta","delta":{"stop_reason":"max_tokens"}}""");
        var calls = stream.CompletedToolCalls();
        Assert.Single(calls);

        // What AgentSession keeps: the assistant turn with the call, then the tool's answer.
        var history = new List<AgentMessage>
        {
            new(AgentRole.User, "list the containers"),
            new(AgentRole.Assistant, null, calls),
            new(AgentRole.Tool, "missing serverId", null, calls[0].Id, true, calls[0].Name),
        };
        var request = new AgentCompletionRequest("model", null, history, [], 1024, 0.2, AgentToolChoice.Auto);

        var anthropic = Record.Exception(() => AnthropicRequestMapper.BuildBody(request, stream: true));
        var gemini = Record.Exception(() => GeminiRequestMapper.BuildBody(request));
        Assert.True(anthropic is null && gemini is null,
            $"the truncated arguments '{calls[0].ArgumentsJson}' are in the history, and building the next request throws " +
            $"(Anthropic: {anthropic?.GetType().Name ?? "ok"}, Gemini: {gemini?.GetType().Name ?? "ok"}): the chat fails on " +
            "every further message until it is cleared");
    }

    private static AgentCompletionRequest HistoryWith(string? argumentsJson) => new("model", null,
        [
            new(AgentRole.User, "go"),
            new(AgentRole.Assistant, null, [new AgentToolCall("call_1", "list_containers", argumentsJson!)]),
            new(AgentRole.Tool, "error", null, "call_1", true, "list_containers"),
        ], [], 1024, 0.2, AgentToolChoice.Auto);

    [Theory]
    [InlineData("{\"serverId\": \"bad")]   // cut off mid-string
    [InlineData("{")]
    [InlineData("[1,2]")]                  // valid JSON, but not an object
    [InlineData("42")]
    [InlineData("")]
    [InlineData(null)]
    public void Every_mapper_sends_an_empty_object_for_arguments_that_are_not_a_json_object(string? argumentsJson)
    {
        var request = HistoryWith(argumentsJson);

        var anthropic = AnthropicRequestMapper.BuildBody(request, stream: true);
        Assert.Equal("{}", anthropic["messages"]![1]!["content"]![0]!["input"]!.ToJsonString());

        var gemini = GeminiRequestMapper.BuildBody(request);
        Assert.Equal("{}", gemini["contents"]![1]!["parts"]![0]!["functionCall"]!["args"]!.ToJsonString());

        var openAi = OpenAiRequestMapper.BuildBody(request, stream: true);
        var arguments = openAi["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.Equal("{}", arguments);
    }

    [Fact]
    public void Well_formed_arguments_are_sent_unchanged()
    {
        var request = HistoryWith("{\"serverId\":\"host-a\",\"all\":true}");

        Assert.Equal("{\"serverId\":\"host-a\",\"all\":true}",
            AnthropicRequestMapper.BuildBody(request, stream: true)["messages"]![1]!["content"]![0]!["input"]!.ToJsonString());
        Assert.Equal("{\"serverId\":\"host-a\",\"all\":true}",
            OpenAiRequestMapper.BuildBody(request, stream: true)["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
    }
}
