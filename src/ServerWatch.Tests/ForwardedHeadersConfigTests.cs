using System.Net;
using ServerWatch.Utils;
using Xunit;

namespace ServerWatch.Tests;

public class ForwardedHeadersConfigTests
{
    [Fact]
    public void ParseTrustedNetworks_Null_ReturnsNonEmptyDefaults()
    {
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(null);

        Assert.NotEmpty(result);
        // Loopback must be trusted by default (reverse proxy on the same host).
        Assert.Contains(result, n => n.Contains(IPAddress.Loopback));
    }

    [Fact]
    public void ParseTrustedNetworks_Empty_ReturnsDefaults()
    {
        // An explicitly empty list must NOT collapse to trust-all — that is the whole vulnerability.
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(System.Array.Empty<string>());

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ParseTrustedNetworks_AllInvalid_ReturnsDefaults()
    {
        // Bad config must fall back to safe defaults, never open trust-all, and never throw.
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(new[] { "nonsense", "999.0.0/8", "" });

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ParseTrustedNetworks_Valid_ParsesExactEntries()
    {
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(new[] { "10.1.2.0/24", "192.168.5.0/24" });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Contains(IPAddress.Parse("10.1.2.55")));
        Assert.Contains(result, n => n.Contains(IPAddress.Parse("192.168.5.9")));
        // A public address is never covered by the configured private ranges.
        Assert.DoesNotContain(result, n => n.Contains(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void ParseTrustedNetworks_MixedValidAndInvalid_KeepsOnlyValid()
    {
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(new[] { "garbage", "10.9.0.0/16" });

        Assert.Single(result);
        Assert.Contains(result, n => n.Contains(IPAddress.Parse("10.9.4.4")));
    }
}
