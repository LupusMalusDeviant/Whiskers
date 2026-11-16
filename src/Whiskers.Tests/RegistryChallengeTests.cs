using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

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

    [Theory]
    [InlineData("docker.io/library/nginx:1.25", "library/nginx", "1.25")]
    [InlineData("docker.io/nginx", "library/nginx", "latest")]
    [InlineData("index.docker.io/grafana/grafana:11.0.0", "grafana/grafana", "11.0.0")]
    [InlineData("nginx:1.25", "library/nginx", "1.25")]
    public void A_docker_hub_image_is_asked_at_the_registry_api_host(string image, string repo, string tag)
    {
        // https://docker.io/v2/ redirects to www.docker.com; the registry API answers at registry-1.docker.io.
        Assert.Equal(("registry-1.docker.io", repo, tag), RegistryClient.ParseImageReference(image));
    }
}
