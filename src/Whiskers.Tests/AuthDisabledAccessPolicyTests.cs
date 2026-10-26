using System.Net;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

// Audit TA-11: with Auth:Disabled every request is an Admin. The policy must keep that to the trusted network and
// stop DNS rebinding and cross-site use of the browser.
public class AuthDisabledAccessPolicyTests
{
    private static readonly AuthDisabledAccessPolicy Default = new(null, null);

    [Theory]
    [InlineData("127.0.0.1", "localhost:5100")]
    [InlineData("::1", "[::1]:5100")]
    [InlineData("192.168.1.20", "192.168.1.5:5100")]
    [InlineData("172.17.0.1", "127.0.0.1:5100")]            // Docker bridge gateway → published port
    [InlineData("100.101.102.103", "100.64.0.7:5100")]      // Tailscale
    public void Trusted_network_with_ip_or_localhost_host_is_allowed(string remote, string host) =>
        Assert.Null(Default.Check(IPAddress.Parse(remote), host, null));

    [Fact]
    public void Public_client_address_is_refused() =>
        Assert.NotNull(Default.Check(IPAddress.Parse("203.0.113.9"), "203.0.113.1:5100", null));

    [Fact]
    public void Ipv4_mapped_public_address_is_refused() =>
        Assert.NotNull(Default.Check(IPAddress.Parse("::ffff:203.0.113.9"), "localhost", null));

    [Fact]
    public void Dns_rebinding_host_name_is_refused() =>
        // attacker.example resolves to the instance's LAN IP; the browser still sends the attacker's name.
        Assert.NotNull(Default.Check(IPAddress.Parse("192.168.1.20"), "attacker.example:5100", null));

    [Fact]
    public void Configured_host_name_is_allowed()
    {
        var policy = new AuthDisabledAccessPolicy(null, new[] { "whiskers.lan" });
        Assert.Null(policy.Check(IPAddress.Parse("192.168.1.20"), "whiskers.lan:5100", "http://whiskers.lan:5100"));
    }

    [Fact]
    public void Cross_site_origin_is_refused_same_origin_allowed()
    {
        var lan = IPAddress.Parse("192.168.1.20");
        Assert.NotNull(Default.Check(lan, "192.168.1.5:5100", "https://evil.example"));
        Assert.Null(Default.Check(lan, "192.168.1.5:5100", "http://192.168.1.5:5100"));
    }

    [Fact]
    public void Narrowed_networks_replace_the_defaults()
    {
        var policy = new AuthDisabledAccessPolicy(new[] { "127.0.0.0/8" }, null);
        Assert.Null(policy.Check(IPAddress.Loopback, "localhost", null));
        Assert.NotNull(policy.Check(IPAddress.Parse("192.168.1.20"), "192.168.1.5", null));
    }

    [Fact]
    public void Local_transport_without_remote_address_is_allowed_but_still_host_checked()
    {
        Assert.Null(Default.Check(null, "localhost", null));
        Assert.NotNull(Default.Check(null, "attacker.example", null));
    }
}
