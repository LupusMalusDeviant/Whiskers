using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part C (ADR-0002 amendment, HOCH-11): the first contact pins the endpoint's host keys into Whiskers'
/// known_hosts and mirrors the preferred one into the record, audited; a different key refuses and keeps both
/// fingerprints; fingerprints come from ssh-keygen; lookups and removals use ssh-keygen (hashed lines) with the bracketed
/// name for a non-default port.
/// </summary>
public sealed class SshHostKeyPinTests : IDisposable
{
    private readonly TempServerStore _store = new();
    private readonly FakeSshToolchain _ssh = new();

    private static readonly string Ed = "ssh-ed25519 " + FakeSshToolchain.KeyText('e');
    private static readonly string Ecdsa = "ecdsa-sha2-nistp256 " + FakeSshToolchain.KeyText('c');
    private static readonly string Rsa = "ssh-rsa " + FakeSshToolchain.KeyText('r');
    private static readonly string OtherEd = "ssh-ed25519 " + FakeSshToolchain.KeyText('x');

    public void Dispose() => _store.Dispose();

    private string KnownHosts => SshHostKeyPolicy.KnownHostsFile(_store.Paths);

    private SshHostKeyPins Pins(ISshToolchain? ssh = null) =>
        new(ssh ?? _ssh, _store.Servers, NullLogger<SshHostKeyPins>.Instance, _store.Paths);

    private async Task<ServerConfig> RecordAsync(int port = 22)
    {
        var record = new ServerConfig
        {
            Id = "web-new", Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = port,
        };
        await _store.Servers.AddServerAsync(record);
        return _store.Servers.GetServer("web-new")!;
    }

    private void KnownHostsHolds(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHosts)!);
        File.WriteAllLines(KnownHosts, lines);
    }

    [Fact]
    public async Task First_contact_pins_and_records()
    {
        var record = await RecordAsync();
        _ssh.LastScan = [Rsa, Ed];
        using var scope = MutationScope.Begin("server.onboard", "op-1");

        var pin = await Pins().PinFirstContactAsync(record, default);

        Assert.Equal(("ssh-ed25519", FakeSshToolchain.FingerprintOf(Ed), SshHostKeySource.FirstContact), (pin.KeyType, pin.FingerprintSha256, pin.Source));
        Assert.Equal("op-1", pin.OperationId);
        var stored = _store.Servers.GetServer("web-new")!;
        Assert.Equal(pin.FingerprintSha256, stored.SshHostKeyFingerprint);
        Assert.Equal(SshHostKeySource.FirstContact, stored.SshHostKeySource);
        Assert.NotNull(stored.SshHostKeyPinnedAtUtc);
        // Every key the endpoint serves is in Whiskers' known_hosts, as plain lines.
        var lines = File.ReadAllLines(KnownHosts);
        Assert.Contains("203.0.113.20 " + Ed, lines);
        Assert.Contains("203.0.113.20 " + Rsa, lines);
        // Audited inside the decision.
        var audit = Assert.Single(scope.Results, r => r.Action == "hostkey.pin");
        Assert.Contains(pin.FingerprintSha256, audit.Details);
    }

    [Fact]
    public async Task Different_existing_key_refuses_and_keeps_both_fingerprints()
    {
        var record = await RecordAsync();
        KnownHostsHolds("203.0.113.20 " + Ed);
        _ssh.LastScan = [OtherEd];

        var mismatch = await Assert.ThrowsAsync<SshHostKeyMismatchException>(() => Pins().PinFirstContactAsync(record, default));

        Assert.Equal(FakeSshToolchain.FingerprintOf(Ed), mismatch.PinnedFingerprint);
        Assert.Equal(FakeSshToolchain.FingerprintOf(OtherEd), mismatch.ServedFingerprint);
        Assert.Equal(["203.0.113.20 " + Ed], File.ReadAllLines(KnownHosts));   // nothing replaced, nothing appended
        Assert.Null(_store.Servers.GetServer("web-new")!.SshHostKeyFingerprint);
    }

    [Fact]
    public async Task Verify_refuses_a_key_other_than_the_recorded_one()
    {
        var record = await RecordAsync();
        _ssh.LastScan = [Ed];
        await Pins().PinFirstContactAsync(record, default);
        _ssh.LastScan = [OtherEd];

        await Assert.ThrowsAsync<SshHostKeyMismatchException>(() => Pins().VerifyAsync(_store.Servers.GetServer("web-new")!, default));
    }

    [Fact]
    public async Task Backfill_writes_no_audit_event()
    {
        var record = await RecordAsync();
        KnownHostsHolds("203.0.113.20 " + Ecdsa);
        using var scope = MutationScope.Begin("server.onboard", "op-2");
        var saves = File.GetLastWriteTimeUtc(Path.Combine(_store.Root, "servers.json"));

        var pin = await Pins().BackfillAsync(record, default);

        Assert.NotNull(pin);
        Assert.Null(pin!.PinnedAtUtc);   // unknown: pinned before Whiskers recorded pins
        Assert.Equal(FakeSshToolchain.FingerprintOf(Ecdsa), pin.FingerprintSha256);
        Assert.Empty(scope.Results);
        Assert.Null(_store.Servers.GetServer("web-new")!.SshHostKeyFingerprint);   // display only, no record write
        Assert.Equal(saves, File.GetLastWriteTimeUtc(Path.Combine(_store.Root, "servers.json")));
    }

    [Fact]
    public async Task Record_mirrors_the_preferred_key_type()
    {
        var record = await RecordAsync();
        _ssh.LastScan = [Rsa, Ecdsa];   // no ed25519: ecdsa before rsa

        var pin = await Pins().PinFirstContactAsync(record, default);

        Assert.Equal("ecdsa-sha2-nistp256", pin.KeyType);
        Assert.Equal("ecdsa-sha2-nistp256", _store.Servers.GetServer("web-new")!.SshHostKeyType);
    }

    [Fact]
    public async Task A_type_the_host_no_longer_offers_is_not_a_mismatch()
    {
        var record = await RecordAsync();
        KnownHostsHolds("203.0.113.20 " + Rsa, "203.0.113.20 " + Ed);
        _ssh.LastScan = [Ed];   // rsa is no longer served; the ed25519 key is the same

        var pin = await Pins().PinFirstContactAsync(record, default);

        Assert.Equal(FakeSshToolchain.FingerprintOf(Ed), pin.FingerprintSha256);
    }

    [Fact]
    public async Task Fingerprint_comes_from_ssh_keygen()
    {
        var runner = new ScriptedSshRunner
        {
            Answer = (file, args) => file == "ssh-keygen" && args[0] == "-l"
                ? new CommandResult { ExitCode = 0, Output = "256 SHA256:fromKeygen0123 no comment (ED25519)\n" }
                : new CommandResult { ExitCode = 0 },
        };

        var fingerprint = await new SshToolchain(runner).FingerprintAsync(Ed + " some comment", default);

        Assert.Equal("SHA256:fromKeygen0123", fingerprint);
        var run = Assert.Single(runner.Runs);
        Assert.Equal("ssh-keygen", run.File);
        Assert.Equal(["-l", "-E", "sha256", "-f"], run.Args.Take(4));
    }

    [Fact]
    public async Task Hashed_known_hosts_lines_are_found_and_removed_with_ssh_keygen()
    {
        // ssh's own lines may be hashed (|1|salt|hash): only ssh-keygen -F / -R find them, never a text search.
        KnownHostsHolds("|1|c2FsdA==|aGFzaA== " + Ed);
        var runner = new ScriptedSshRunner
        {
            Answer = (file, args) => file == "ssh-keygen" && args[0] == "-F"
                ? new CommandResult { ExitCode = 0, Output = "# Host 203.0.113.20 found: line 1\n|1|c2FsdA==|aGFzaA== " + Ed + "\n" }
                : new CommandResult { ExitCode = 0 },
        };
        var ssh = new SshToolchain(runner);

        var found = await ssh.KnownHostsLookupAsync("203.0.113.20", KnownHosts, default);
        await ssh.KnownHostsRemoveAsync("203.0.113.20", KnownHosts, default);

        Assert.Equal([Ed], found);
        Assert.Equal(["-F", "203.0.113.20", "-f", KnownHosts], runner.Runs[0].Args);
        Assert.Equal(["-R", "203.0.113.20", "-f", KnownHosts], runner.Runs[1].Args);
        Assert.All(runner.Runs, r => Assert.Equal("ssh-keygen", r.File));
    }

    [Fact]
    public async Task Non_default_port_is_looked_up_as_bracketed_host_port()
    {
        var record = await RecordAsync(port: 2222);
        _ssh.LastScan = [Ed];

        await Pins().PinFirstContactAsync(record, default);

        Assert.Contains(_ssh.Lookups, l => l.Name == "[203.0.113.20]:2222");
        Assert.Contains("[203.0.113.20]:2222 " + Ed, File.ReadAllLines(KnownHosts));
        Assert.Equal("[203.0.113.20]:2222", SshHostKeys.EndpointName("203.0.113.20", 2222));
        Assert.Equal("203.0.113.20", SshHostKeys.EndpointName("203.0.113.20", 22));
    }

    [Fact]
    public async Task Pin_from_disk_records_the_rescue_source()
    {
        var record = await RecordAsync();

        var pin = await Pins().PinFromDiskAsync(record, [Ed + " root@host"], default);

        Assert.Equal(SshHostKeySource.RescueDisk, pin.Source);
        Assert.Equal(SshHostKeySource.RescueDisk, _store.Servers.GetServer("web-new")!.SshHostKeySource);
        Assert.Empty(_ssh.SshCalls);
    }
}
