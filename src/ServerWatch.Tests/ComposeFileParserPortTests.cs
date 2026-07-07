using ServerWatch.Models;
using ServerWatch.Services.Deployment;

namespace ServerWatch.Tests;

public class ComposeFileParserPortTests
{
    private static DeploymentValidationResult ParseWithPort(string portLine)
    {
        var yaml = "services:\n  web:\n    image: nginx:latest\n    ports:\n      - \"" + portLine + "\"\n";
        return ComposeFileParser.Parse(yaml);
    }

    [Fact]
    public void LoopbackBind_ThreadsBindIp()
    {
        var r = ParseWithPort("127.0.0.1:8080:80");
        Assert.True(r.IsValid);
        var svc = Assert.Single(r.Services);
        Assert.Equal("80", svc.PortMappings["8080"]);
        Assert.Equal("127.0.0.1", svc.PortBindIps["8080"]);
    }

    [Fact]
    public void TwoPart_MapsHostToContainer_NoBindIp()
    {
        var svc = Assert.Single(ParseWithPort("8080:80").Services);
        Assert.Equal("80", svc.PortMappings["8080"]);
        Assert.Empty(svc.PortBindIps);
    }

    [Fact]
    public void ProtocolSuffix_IsStripped()
    {
        var svc = Assert.Single(ParseWithPort("8080:80/tcp").Services);
        Assert.Equal("80", svc.PortMappings["8080"]);
    }

    [Fact]
    public void SinglePort_PublishesSamePort()
    {
        // Previously dropped silently → a stack that "deployed" with no ports.
        var svc = Assert.Single(ParseWithPort("80").Services);
        Assert.Equal("80", svc.PortMappings["80"]);
    }

    [Fact]
    public void MalformedPort_FailsLoudly()
    {
        var r = ParseWithPort("1:2:3:4");
        Assert.False(r.IsValid);
        Assert.NotEmpty(r.Errors);
    }
}
