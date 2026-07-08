using System.IO;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;

namespace Whiskers.Tests;

public class AgentTranscriptStoreTests
{
    private static AgentTranscriptStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "sw-agent-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Round_trips_messages_with_tool_calls_and_enum()
    {
        var store = TempStore();
        var messages = new List<AgentMessage>
        {
            new(AgentRole.User, "hallo"),
            new(AgentRole.Assistant, null,
                ToolCalls: new[] { new AgentToolCall("c1", "stop_container", "{\"id\":\"x\"}") }),
            new(AgentRole.Tool, "ok", ToolCallId: "c1", IsError: false, ToolName: "stop_container"),
        };

        await store.SaveAsync("u@example.com", messages);
        var loaded = await store.LoadAsync("u@example.com");

        Assert.Equal(3, loaded.Count);
        Assert.Equal(AgentRole.User, loaded[0].Role);
        Assert.Equal("hallo", loaded[0].Text);
        Assert.Equal("stop_container", loaded[1].ToolCalls![0].Name);
        Assert.Equal("{\"id\":\"x\"}", loaded[1].ToolCalls![0].ArgumentsJson);
        Assert.Equal("c1", loaded[2].ToolCallId);
        Assert.Equal("stop_container", loaded[2].ToolName);
    }

    [Fact]
    public async Task Clear_removes_history()
    {
        var store = TempStore();
        await store.SaveAsync("x@y", new List<AgentMessage> { new(AgentRole.User, "hi") });
        await store.ClearAsync("x@y");
        Assert.Empty(await store.LoadAsync("x@y"));
    }

    [Fact]
    public async Task Unknown_user_loads_empty()
    {
        Assert.Empty(await TempStore().LoadAsync("nobody@nowhere"));
    }

    [Fact] // MIT-31: sanitize before persisting/re-seeding
    public async Task Save_drops_leading_tool_orphan_toolcalls_redacts_and_strips_images()
    {
        var store = TempStore();
        var messages = new List<AgentMessage>
        {
            new(AgentRole.Tool, "leading orphan", ToolCallId: "old", ToolName: "x"),          // leading Tool → dropped
            new(AgentRole.User, "hi", ImageBase64: "AAAA", ImageMediaType: "image/png"),      // image → stripped
            new(AgentRole.Assistant, "thinking",
                ToolCalls: new[] { new AgentToolCall("c1", "t", "{}"), new AgentToolCall("c2", "t", "{}") }), // c2 unanswered → stripped
            new(AgentRole.Tool, "MYSQL_PWD=sup3rs3cret done", ToolCallId: "c1", ToolName: "t"),// secret → redacted
        };

        await store.SaveAsync("u@x", messages);
        var loaded = await store.LoadAsync("u@x");

        Assert.Equal(AgentRole.User, loaded[0].Role);               // leading Tool dropped
        Assert.Null(loaded[0].ImageBase64);                          // screenshot stripped
        var assistant = loaded.Single(m => m.Role == AgentRole.Assistant);
        Assert.Single(assistant.ToolCalls!);                         // only the answered c1 survives
        Assert.Equal("c1", assistant.ToolCalls![0].Id);
        var tool = loaded.Last(m => m.Role == AgentRole.Tool);
        Assert.DoesNotContain("sup3rs3cret", tool.Text);             // tool output redacted
    }
}
