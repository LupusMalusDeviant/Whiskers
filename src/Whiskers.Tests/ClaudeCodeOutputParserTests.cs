using System.Linq;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;

namespace Whiskers.Tests;

public class ClaudeCodeOutputParserTests
{
    [Fact]
    public void Assistant_text_becomes_delta()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hallo\"}]}}");
        var delta = Assert.IsType<AgentEvent.AssistantDelta>(events.Single());
        Assert.Equal("Hallo", delta.Text);
    }

    [Fact]
    public void Assistant_tool_use_becomes_proposed()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"list_containers\",\"input\":{\"x\":1}}]}}");
        var proposed = Assert.IsType<AgentEvent.ToolProposed>(events.Single());
        Assert.Equal("toolu_1", proposed.Call.Id);
        Assert.Equal("list_containers", proposed.Call.Name);
        Assert.Contains("\"x\"", proposed.Call.ArgumentsJson);
    }

    [Fact]
    public void Tool_result_becomes_executed_with_error_flag()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"is_error\":true,\"content\":\"boom\"}]}}");
        var exec = Assert.IsType<AgentEvent.ToolExecuted>(events.Single());
        Assert.Equal("toolu_1", exec.Result.ToolCallId);
        Assert.True(exec.Result.IsError);
        Assert.Equal("boom", exec.Result.Content);
    }

    [Fact]
    public void Tool_result_content_array_is_concatenated()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}]}}");
        var exec = Assert.IsType<AgentEvent.ToolExecuted>(events.Single());
        Assert.Equal("ab", exec.Result.Content);
    }

    [Fact]
    public void Result_success_completes_turn()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\"}");
        Assert.IsType<AgentEvent.TurnCompleted>(events.Single());
    }

    [Fact]
    public void Result_error_becomes_failed()
    {
        var events = ClaudeCodeOutputParser.ParseLine(
            "{\"type\":\"result\",\"is_error\":true,\"result\":\"kaputt\"}");
        var failed = Assert.IsType<AgentEvent.Failed>(events.Single());
        Assert.Equal("kaputt", failed.Message);
    }

    [Theory]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Noise_lines_produce_no_events(string line)
    {
        Assert.Empty(ClaudeCodeOutputParser.ParseLine(line));
    }
}
