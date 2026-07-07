using ServerWatch.Services.Agent.Providers;
using Xunit;

namespace ServerWatch.Tests;

public class ProviderErrorTests
{
    [Fact]
    public void Extract_OpenAiShape_PullsMessage()
    {
        var body = """{"error":{"message":"model not found","type":"invalid_request_error"}}""";
        Assert.Equal("model not found", ProviderError.Extract(body));
    }

    [Fact]
    public void Extract_AnthropicShape_PullsMessage()
    {
        var body = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
        Assert.Equal("invalid x-api-key", ProviderError.Extract(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_EmptyBody_ReturnsPlaceholder(string body)
    {
        Assert.Equal("(leere Antwort)", ProviderError.Extract(body));
    }

    [Fact]
    public void Extract_NonJsonBody_FallsBackToTrimmedBody_NoThrow()
    {
        // An upstream proxy might return HTML/plain text — must not throw, just surface the body.
        Assert.Equal("502 Bad Gateway", ProviderError.Extract("502 Bad Gateway"));
    }

    [Fact]
    public void Extract_NoSecretLeak()
    {
        // The error body never carries the API key (the key is a request header), and the extractor
        // returns only error.message — so a key string can never appear in the surfaced error.
        const string apiKey = "sk-ant-SECRETKEY123";
        var body = """{"error":{"message":"invalid request: contents must not be empty"}}""";

        var result = ProviderError.Extract(body);

        Assert.Equal("invalid request: contents must not be empty", result);
        Assert.DoesNotContain(apiKey, result);
    }

    [Fact]
    public void Extract_LongMessage_Truncated()
    {
        var longMsg = new string('x', 600);
        var body = "{\"error\":{\"message\":\"" + longMsg + "\"}}";

        var result = ProviderError.Extract(body);

        Assert.True(result.Length <= 501);
        Assert.EndsWith("…", result);
    }
}
