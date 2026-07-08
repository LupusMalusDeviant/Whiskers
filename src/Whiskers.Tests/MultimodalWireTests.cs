using System.Text.Json.Nodes;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class MultimodalWireTests
{
    private const string Img = "AAAABBBBCCCC"; // dummy base64

    private static AgentCompletionRequest Req() => new(
        "model", "sys",
        new[] { new AgentMessage(AgentRole.User, "Was siehst du?", ImageBase64: Img, ImageMediaType: "image/png") },
        Array.Empty<AgentToolDefinition>());

    [Fact]
    public void OpenAi_user_message_carries_image_url_data_uri()
    {
        var msg = OpenAiRequestMapper.BuildBody(Req(), stream: false)["messages"]!.AsArray()
            .First(m => (string?)m!["role"] == "user")!;
        var content = msg["content"]!.AsArray();
        Assert.Equal("text", (string)content[0]!["type"]!);
        Assert.Equal("image_url", (string)content[1]!["type"]!);
        Assert.Equal($"data:image/png;base64,{Img}", (string)content[1]!["image_url"]!["url"]!);
    }

    [Fact]
    public void Anthropic_user_message_carries_base64_image_source()
    {
        var msg = AnthropicRequestMapper.BuildBody(Req(), stream: false)["messages"]!.AsArray()
            .First(m => (string?)m!["role"] == "user")!;
        var content = msg["content"]!.AsArray();
        var image = content.First(c => (string?)c!["type"] == "image")!;
        Assert.Equal("base64", (string)image["source"]!["type"]!);
        Assert.Equal("image/png", (string)image["source"]!["media_type"]!);
        Assert.Equal(Img, (string)image["source"]!["data"]!);
    }

    [Fact]
    public void Gemini_user_message_carries_inline_data()
    {
        var content = GeminiRequestMapper.BuildBody(Req())["contents"]!.AsArray()
            .First(c => (string?)c!["role"] == "user")!;
        var parts = content["parts"]!.AsArray();
        var inline = parts.First(p => p!["inline_data"] is not null)!;
        Assert.Equal("image/png", (string)inline["inline_data"]!["mime_type"]!);
        Assert.Equal(Img, (string)inline["inline_data"]!["data"]!);
    }

    [Fact]
    public void No_image_keeps_plain_string_content()
    {
        var req = new AgentCompletionRequest("m", "s",
            new[] { new AgentMessage(AgentRole.User, "hallo") }, Array.Empty<AgentToolDefinition>());
        var msg = OpenAiRequestMapper.BuildBody(req, stream: false)["messages"]!.AsArray()
            .First(m => (string?)m!["role"] == "user")!;
        Assert.Equal("hallo", (string)msg["content"]!); // plain string, not an array
    }
}
