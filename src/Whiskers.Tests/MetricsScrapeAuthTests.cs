using Whiskers.Utils;
using Xunit;

namespace Whiskers.Tests;

public class MetricsScrapeAuthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_NoConfiguredToken_Disabled(string? token)
    {
        // No token configured => endpoint is opt-in and stays disabled, regardless of the request.
        Assert.Equal(MetricsScrapeAuthResult.Disabled, MetricsScrapeAuth.Check(token, "Bearer anything"));
    }

    [Fact]
    public void Check_ValidToken_Ok()
    {
        Assert.Equal(MetricsScrapeAuthResult.Ok, MetricsScrapeAuth.Check("s3cr3t", "Bearer s3cr3t"));
    }

    [Theory]
    [InlineData(null)]              // no header
    [InlineData("")]               // empty header
    [InlineData("s3cr3t")]         // missing "Bearer " prefix
    [InlineData("Bearer wrong")]   // wrong token
    [InlineData("Basic s3cr3t")]   // wrong scheme
    [InlineData("bearer s3cr3t")]  // case-sensitive scheme
    public void Check_MissingOrWrong_Unauthorized(string? header)
    {
        Assert.Equal(MetricsScrapeAuthResult.Unauthorized, MetricsScrapeAuth.Check("s3cr3t", header));
    }

    [Fact]
    public void Check_WrongLengthToken_NoThrowAndUnauthorized()
    {
        // FixedTimeEquals handles differing lengths without throwing; the result is a plain enum that
        // never carries the secret — no token leaks into an exception, log, or return value.
        var result = MetricsScrapeAuth.Check("short", "Bearer muchlongerpresentedtoken");

        Assert.Equal(MetricsScrapeAuthResult.Unauthorized, result);
    }
}
