using ServerWatch.Services.ImageUpdate;

namespace ServerWatch.Tests;

public class RegistryChallengeTests
{
    [Fact]
    public void ParsesDockerHubChallenge()
    {
        var c = RegistryClient.ParseBearerChallenge(
            "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");
        Assert.Equal("https://auth.docker.io/token", c["realm"]);
        Assert.Equal("registry.docker.io", c["service"]);
        Assert.Equal("repository:library/nginx:pull", c["scope"]);
    }

    [Fact]
    public void ParsesGhcrChallenge_KeysAreCaseInsensitive()
    {
        var c = RegistryClient.ParseBearerChallenge(
            "realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:owner/repo:pull\"");
        Assert.Equal("https://ghcr.io/token", c["REALM"]);
        Assert.Equal("ghcr.io", c["service"]);
    }

    [Fact]
    public void ScopeWithCommaInsideQuotes_IsPreserved()
    {
        // A naive comma-split would corrupt this; the regex keeps the quoted value intact.
        var c = RegistryClient.ParseBearerChallenge(
            "realm=\"https://r/token\",service=\"r\",scope=\"repository:x:pull,push\"");
        Assert.Equal("repository:x:pull,push", c["scope"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Basic realm=unquoted")]
    public void EmptyOrGarbage_ReturnsEmpty(string param)
    {
        Assert.Empty(RegistryClient.ParseBearerChallenge(param));
    }
}
