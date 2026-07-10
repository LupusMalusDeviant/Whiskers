using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Models.Hostinger;
using Whiskers.Services.Cloud.Providers;

namespace Whiskers.Tests;

/// <summary>C10 — the destructive-op TARGET resolution: which cloud VM a Whiskers server maps to. Public-IP
/// match first, then a self-flagging name fallback (a mesh/Tailscale SshHost ≠ the cloud public IP, so a name
/// match is easy to get wrong). Pure logic, no HTTP — the safety net for the provider seam, since power-off /
/// hard-reset / snapshot all resolve their target through here, and hitting the wrong VM would be destructive.</summary>
public class CloudTargetResolutionTests
{
    private static ServerConfig Sw(string name, string host) => new() { Id = "sw-" + name, Name = name, SshHost = host };

    // ─────────────────────────────── Hetzner ───────────────────────────────
    private static HetznerServer Hz(long id, string name, string? ipv4) => new()
    {
        Id = id, Name = name, Status = "running",
        PublicNet = ipv4 == null ? null : new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = ipv4 } }
    };

    [Fact]
    public void Hetzner_matches_by_public_ip_with_no_warning()
    {
        var info = HetznerCloudProvider.Map(Sw("web", "203.0.113.5"),
            new List<HetznerServer> { Hz(1, "db", "203.0.113.1"), Hz(2, "web", "203.0.113.5") });
        Assert.NotNull(info);
        Assert.Equal(2, info!.CloudId);   // matched the right VM by IP, not by name
        Assert.Null(info.Note);           // strong (IP) match → no weak-resolution warning
    }

    [Fact]
    public void Hetzner_falls_back_to_name_and_flags_the_weak_match()
    {
        // SshHost is a mesh IP matching no VM's public IP → name fallback, which MUST surface a warning.
        var info = HetznerCloudProvider.Map(Sw("web", "100.64.0.9"),
            new List<HetznerServer> { Hz(1, "db", "203.0.113.1"), Hz(2, "web", "203.0.113.5") });
        Assert.NotNull(info);
        Assert.Equal(2, info!.CloudId);   // resolved by name
        Assert.NotNull(info.Note);        // weak (name) match → warning surfaced to the caller
    }

    [Fact]
    public void Hetzner_no_match_returns_null_never_an_arbitrary_vm()
    {
        var info = HetznerCloudProvider.Map(Sw("web", "100.64.0.9"),
            new List<HetznerServer> { Hz(1, "db", "203.0.113.1") });
        Assert.Null(info);   // neither IP nor name → no target, so no destructive op fires
    }

    // ─────────────────────────────── Hostinger ───────────────────────────────
    private static HostingerVm Hv(long id, string hostname, string? ip) => new()
    {
        Id = id, Hostname = hostname, State = "running",
        Ipv4 = ip == null ? null : new List<HostingerIp> { new() { Address = ip } }
    };

    [Fact]
    public void Hostinger_matches_by_public_ip_with_no_warning()
    {
        var info = HostingerCloudProvider.Map(Sw("app", "198.51.100.7"),
            new List<HostingerVm> { Hv(1, "other", "198.51.100.1"), Hv(2, "app", "198.51.100.7") });
        Assert.NotNull(info);
        Assert.Equal(2, info!.CloudId);
        Assert.Null(info.Note);
    }

    [Fact]
    public void Hostinger_falls_back_to_name_and_flags_the_weak_match()
    {
        var info = HostingerCloudProvider.Map(Sw("app", "100.64.0.3"),
            new List<HostingerVm> { Hv(2, "app", "198.51.100.7") });
        Assert.NotNull(info);
        Assert.Equal(2, info!.CloudId);
        Assert.NotNull(info.Note);
    }

    [Fact]
    public void Hostinger_no_match_returns_null_never_an_arbitrary_vm()
    {
        var info = HostingerCloudProvider.Map(Sw("app", "100.64.0.3"),
            new List<HostingerVm> { Hv(1, "other", "198.51.100.1") });
        Assert.Null(info);
    }
}
