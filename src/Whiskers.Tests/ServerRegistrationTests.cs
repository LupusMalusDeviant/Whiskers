using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part B (D7, D10, D11): a server is registered from name and public IP. The IP is a public literal;
/// the cloud credential is found by the VM's exact public IP in exactly one stored credential (never by name) and
/// shared by its vault reference — no secret is copied or written.
/// </summary>
public sealed class ServerRegistrationTests : IDisposable
{
    private readonly TempServerStore _store = new();
    private readonly Dictionary<string, List<HetznerServer>> _accounts = new(StringComparer.Ordinal);
    private readonly IHetznerService _hetzner;
    private readonly ScriptedProxy<IHetznerService> _script;

    public ServerRegistrationTests()
    {
        (_hetzner, _script) = ScriptedProxy<IHetznerService>.Create();
        _script.On(nameof(IHetznerService.ListServersAsync), a =>
            _accounts.TryGetValue((string)a[0]!, out var vms) ? vms : throw new HttpRequestException("unknown token"));
    }

    public void Dispose() => _store.Dispose();

    private ServerRegistration Registration() => new(_store.Servers, new TestServices(_hetzner, _store.Vault));

    private static HetznerServer Vm(long id, string name, string ip) => new()
    {
        Id = id, Name = name, Status = "running", Created = "2026-01-02T03:04:05+00:00",
        PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = ip } },
    };

    /// <summary>An existing Hetzner server whose token lives in the vault under a reference; the token is made per run.</summary>
    private async Task<(string Ref, string Token)> AccountAsync(string serverId, string ip, params HetznerServer[] vms)
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        var reference = $"cloudkey:{serverId}:{Guid.NewGuid():N}";
        await _store.Vault.SetSecretAsync(reference, token);
        _accounts[token] = vms.ToList();
        await _store.Servers.AddServerAsync(new ServerConfig
        {
            Id = serverId, Name = serverId, ConnectionType = ConnectionType.SSH, SshHost = ip, SshUser = "root",
            CloudProvider = CloudProvider.Hetzner, CloudKeyRef = reference,
        });
        return (reference, token);
    }

    private static ServerRegistrationRequest Request(string ip, string name = "web-new", CloudProvider provider = CloudProvider.Hetzner,
        string? from = null) => new(name, ip, null, provider, from);

    public static TheoryData<string> Refused => new()
    {
        "example.org", "web01", "10.1", "203.0.113", "127.0.0.1", "0.0.0.0", "10.0.0.5", "172.16.3.4", "172.31.255.1",
        "192.168.1.1", "169.254.1.1", "100.64.0.9", "100.127.0.1", "224.0.0.1", "255.255.255.255", "::1", "fe80::1",
        "fd00::1", "fc00::5", "ff02::1", "fe80::1%eth0", "203.0.113.7 ", "203.0.113.7;id", "",
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Ip_must_be_a_public_literal(string ip)
    {
        // A trailing blank is trimmed, everything else listed here is refused.
        if (ip == "203.0.113.7 ") { Assert.Equal("203.0.113.7", ServerRegistration.PublicIp(ip, CloudProvider.None)); return; }
        Assert.Throws<ServerRegistrationException>(() => ServerRegistration.PublicIp(ip, CloudProvider.None));
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("198.51.100.20")]
    [InlineData("2001:db8::7")]
    public void A_public_literal_is_accepted_in_normal_form(string ip) =>
        Assert.Equal(ip, ServerRegistration.PublicIp(ip, CloudProvider.None));

    [Fact]
    public void Hetzner_registration_accepts_only_ipv4()
    {
        Assert.Throws<ServerRegistrationException>(() => ServerRegistration.PublicIp("2001:db8::7", CloudProvider.Hetzner));
        Assert.Equal("203.0.113.7", ServerRegistration.PublicIp("203.0.113.7", CloudProvider.Hetzner));
    }

    [Fact]
    public async Task Duplicate_ip_or_vm_is_refused()
    {
        await AccountAsync("srv-a", "203.0.113.10", Vm(10, "srv-a", "203.0.113.10"), Vm(20, "web-new", "203.0.113.20"));

        // The IP of an existing record.
        await Assert.ThrowsAsync<ServerRegistrationException>(() => Registration().AddAsync(Request("203.0.113.10"), default));

        // The VM an existing record is bound to (by CloudServerId), reached under another IP.
        var bound = _store.Servers.GetServer("srv-a")!.Clone();
        bound.CloudServerId = 20;
        await _store.Servers.UpdateServerAsync(bound);
        await Assert.ThrowsAsync<ServerRegistrationException>(() => Registration().AddAsync(Request("203.0.113.20"), default));
        Assert.Single(_store.Servers.GetServers());
    }

    [Fact]
    public async Task Id_slug_is_unique()
    {
        var first = await Registration().AddAsync(Request("203.0.113.31", "Web New", CloudProvider.None), default);
        var second = await Registration().AddAsync(Request("203.0.113.32", "web new", CloudProvider.None), default);

        Assert.Equal("web-new", first.ServerId);
        Assert.Equal("web-new-2", second.ServerId);
        var record = _store.Servers.GetServer("web-new")!;
        Assert.Equal(ConnectionType.SSH, record.ConnectionType);
        Assert.Equal(("root", 22, "203.0.113.31"), (record.SshUser, record.SshPort, record.SshHost));
        Assert.Null(record.SshPassword);
        Assert.Null(record.SshKeyFileName);
    }

    [Fact]
    public async Task Credential_found_by_exact_ip_in_exactly_one_account()
    {
        var a = await AccountAsync("srv-a", "203.0.113.10", Vm(10, "srv-a", "203.0.113.10"), Vm(20, "web-new", "203.0.113.20"));
        await AccountAsync("srv-b", "203.0.113.11", Vm(11, "srv-b", "203.0.113.11"), Vm(21, "other", "203.0.113.21"));

        var result = await Registration().AddAsync(Request("203.0.113.20"), default);

        Assert.Equal("srv-a", result.CredentialFrom);
        Assert.Equal(20, result.Cloud!.CloudServerId);
        var record = _store.Servers.GetServer(result.ServerId)!;
        Assert.Equal(20, record.CloudServerId);
        Assert.Equal(a.Ref, record.CloudKeyRef);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Zero_or_two_matches_refuse()
    {
        await AccountAsync("srv-a", "203.0.113.10", Vm(10, "srv-a", "203.0.113.10"), Vm(20, "web-new", "203.0.113.20"));
        await AccountAsync("srv-b", "203.0.113.11", Vm(11, "srv-b", "203.0.113.11"), Vm(21, "web-new", "203.0.113.20"));

        // Two accounts report a VM with the IP: refused, the candidates named.
        var two = await Assert.ThrowsAsync<ServerRegistrationException>(() => Registration().AddAsync(Request("203.0.113.20"), default));
        Assert.Contains("srv-a", two.Message);
        Assert.Contains("srv-b", two.Message);

        // No account has a VM with the IP.
        await Assert.ThrowsAsync<ServerRegistrationException>(() => Registration().AddAsync(Request("203.0.113.99"), default));
        Assert.Equal(2, _store.Servers.GetServers().Count);

        // Naming the credential resolves the ambiguity.
        var named = await Registration().AddAsync(Request("203.0.113.20", from: "srv-b"), default);
        Assert.Equal(21, named.Cloud!.CloudServerId);
    }

    [Fact]
    public async Task Name_match_is_never_used()
    {
        // A VM with the requested name but another IP is no match: the registration is refused, nothing is bound.
        await AccountAsync("srv-a", "203.0.113.10", Vm(10, "srv-a", "203.0.113.10"), Vm(20, "web-new", "203.0.113.20"));

        await Assert.ThrowsAsync<ServerRegistrationException>(() => Registration().AddAsync(Request("203.0.113.44", "web-new"), default));

        Assert.DoesNotContain(_store.Servers.GetServers(), s => s.CloudServerId == 20);
    }

    [Fact]
    public async Task New_record_shares_the_vault_reference_and_writes_no_secret()
    {
        var a = await AccountAsync("srv-a", "203.0.113.10", Vm(10, "srv-a", "203.0.113.10"), Vm(20, "web-new", "203.0.113.20"));
        var secretsBefore = _store.Vault.ListSecrets().Select(e => e.Key).Order(StringComparer.Ordinal).ToList();

        var result = await Registration().AddAsync(Request("203.0.113.20"), default);

        var record = _store.Servers.GetServer(result.ServerId)!;
        Assert.Equal(a.Ref, record.CloudKeyRef);
        Assert.Null(record.CloudApiKey);
        Assert.Equal(secretsBefore, _store.Vault.ListSecrets().Select(e => e.Key).Order(StringComparer.Ordinal).ToList());
        // The token is in no field of the stored file.
        Assert.DoesNotContain(a.Token, File.ReadAllText(Path.Combine(_store.Root, "servers.json")));
    }

    [Fact]
    public async Task Without_a_stored_credential_the_server_is_registered_unbound_with_a_warning()
    {
        var result = await Registration().AddAsync(Request("203.0.113.50"), default);

        Assert.Null(result.Cloud);
        Assert.Single(result.Warnings);
        Assert.Null(_store.Servers.GetServer(result.ServerId)!.CloudServerId);
    }
}
