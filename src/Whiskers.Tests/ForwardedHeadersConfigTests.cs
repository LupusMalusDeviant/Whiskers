using System.Net;
using Microsoft.Extensions.Configuration;
using Whiskers.Utils;
using Xunit;

namespace Whiskers.Tests;

/// <summary>R1-50 (A-15): which peers' forwarded headers are trusted. Without configuration only loopback; a configured
/// list is taken as written; an entry that cannot be read refuses the start. The pipeline-level proof (a spoofed
/// X-Forwarded-For does not move the login rate limit) is in <see cref="HttpSecurityTests"/>.</summary>
public class ForwardedHeadersConfigTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" |  ")]
    public void Without_configuration_only_loopback_is_trusted(string entries)
    {
        string[]? configured = entries == "null" ? null : entries.Length == 0 ? [] : entries.Split('|');
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(configured);

        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Loopback));
        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.IPv6Loopback));
        // Before R1-50 every private range was trusted: any client in the LAN or tailnet could claim another address.
        foreach (var lan in new[] { "10.0.0.5", "172.17.0.2", "192.168.1.10", "100.64.1.1", "8.8.8.8" })
            Assert.False(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse(lan)), lan);
    }

    [Fact]
    public void A_configured_list_is_taken_as_written_cidrs_and_single_addresses()
    {
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(new[] { "10.1.2.0/24", " 172.18.0.7 ", "fd00::/8" });

        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("10.1.2.55")));
        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("172.18.0.7")));
        Assert.False(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("172.18.0.8")));
        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("fd00::1")));
        // Configured means configured: loopback is not added on top.
        Assert.False(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Loopback));
        Assert.False(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("999.0.0.0/8")]
    [InlineData("10.0.0.0/33")]
    // Shorthand forms IPAddress.TryParse accepts would trust a different address than the one meant.
    [InlineData("172.18.0")]
    [InlineData("10")]
    [InlineData("0x7f000001")]
    [InlineData("10.1/16")]
    public void An_entry_that_cannot_be_read_refuses_the_start_instead_of_trusting_another_set(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ForwardedHeadersConfig.ParseTrustedNetworks(new[] { "10.9.0.0/16", bad }));

        Assert.Contains(bad, ex.Message);
        Assert.Contains(ForwardedHeadersConfig.SectionName, ex.Message);
    }

    [Fact]
    public void Configuration_is_read_from_indexed_entries_or_one_comma_separated_value()
    {
        static IReadOnlyList<System.Net.IPNetwork> Read(params (string Key, string Value)[] values) =>
            ForwardedHeadersConfig.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

        var indexed = Read(("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"), ("ForwardedHeaders:TrustedNetworks:1", "172.18.0.2"));
        Assert.True(ForwardedHeadersConfig.IsTrusted(indexed, IPAddress.Parse("10.4.4.4")));
        Assert.True(ForwardedHeadersConfig.IsTrusted(indexed, IPAddress.Parse("172.18.0.2")));

        // One value without index (ForwardedHeaders__TrustedNetworks=…) used to bind to nothing and fall back silently.
        var scalar = Read(("ForwardedHeaders:TrustedNetworks", "10.0.0.0/8, 172.18.0.2"));
        Assert.True(ForwardedHeadersConfig.IsTrusted(scalar, IPAddress.Parse("10.4.4.4")));
        Assert.True(ForwardedHeadersConfig.IsTrusted(scalar, IPAddress.Parse("172.18.0.2")));
        Assert.False(ForwardedHeadersConfig.IsTrusted(scalar, IPAddress.Loopback));

        Assert.Throws<InvalidOperationException>(() => Read(("ForwardedHeaders:TrustedNetworks", "10.0.0.0/8,proxy")));
        Assert.True(ForwardedHeadersConfig.IsTrusted(Read(), IPAddress.Loopback));
    }

    [Fact]
    public void An_ipv4_mapped_peer_is_judged_by_its_ipv4_address()
    {
        var result = ForwardedHeadersConfig.ParseTrustedNetworks(null);

        Assert.True(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Loopback.MapToIPv6()));
        Assert.False(ForwardedHeadersConfig.IsTrusted(result, IPAddress.Parse("10.0.0.5").MapToIPv6()));
        Assert.False(ForwardedHeadersConfig.IsTrusted(result, null));
    }
}
