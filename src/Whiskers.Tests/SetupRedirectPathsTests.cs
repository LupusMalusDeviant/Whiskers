using Microsoft.AspNetCore.Http;
using Whiskers.Startup;

namespace Whiskers.Tests;

public class SetupRedirectPathsTests
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/set-culture")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/mcp")]
    [InlineData("/api/webhooks/abc")]
    [InlineData("/metrics")]
    public void Infra_paths_are_exempt(string path) => Assert.True(SetupRedirectPaths.IsExempt(path));

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/settings")]
    [InlineData("/")]
    public void App_paths_are_not_exempt(string path) => Assert.False(SetupRedirectPaths.IsExempt(path));

    [Fact]
    public void Html_get_is_a_navigation()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Headers.Accept = "text/html,application/xhtml+xml";
        Assert.True(SetupRedirectPaths.IsHtmlNavigation(ctx.Request));
    }

    [Fact]
    public void Non_html_and_non_get_are_not_navigations()
    {
        var css = new DefaultHttpContext(); css.Request.Method = "GET"; css.Request.Headers.Accept = "text/css";
        Assert.False(SetupRedirectPaths.IsHtmlNavigation(css.Request));   // fingerprinted /app.<hash>.css
        var post = new DefaultHttpContext(); post.Request.Method = "POST"; post.Request.Headers.Accept = "text/html";
        Assert.False(SetupRedirectPaths.IsHtmlNavigation(post.Request));
    }
}
