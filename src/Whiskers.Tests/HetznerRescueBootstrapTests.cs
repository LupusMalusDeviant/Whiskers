using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part D (D1, D2, INV-64): the Hetzner rescue bootstrap. Fresh servers only, the VM by its bound id and
/// exact IP, a confirmed name when it differs; the key generated in a memory-backed directory and sealed; a reboot only
/// counts once the port or the host key went away (else one hard reset); whatever fails after rescue was enabled,
/// rescue is disabled, the VM reset and the project key deleted. The rescue password and the rescue host key go nowhere.
/// </summary>
public sealed class HetznerRescueBootstrapTests : IDisposable
{
    private const long VmId = 20;
    private const long ProjectKeyId = 77;
    private static readonly string HostKey = "ssh-ed25519 " + FakeSshToolchain.KeyText('h');
    private static readonly string RescueKey = "ssh-ed25519 " + FakeSshToolchain.KeyText('q');

    private readonly TempServerStore _store = new();
    private readonly FakeSshToolchain _ssh = new();
    private readonly AcceptingHostKeyPins _pins = new();
    private readonly IHetznerService _hetzner;
    private readonly ScriptedProxy<IHetznerService> _cloud;
    private readonly string _keyBase;
    private readonly string _password = "rescue-" + Guid.NewGuid().ToString("N");
    private readonly ListLogger _log = new();
    private HetznerServer _vm = new()
    {
        Id = VmId, Name = "web-new", Status = "running", Created = "2026-01-02T03:04:05+00:00",
        PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = "203.0.113.20" } },
    };

    public HetznerRescueBootstrapTests()
    {
        _keyBase = Path.Combine(_store.Root, "shm");
        Directory.CreateDirectory(_keyBase);
        (_hetzner, _cloud) = ScriptedProxy<IHetznerService>.Create();
        _cloud.On(nameof(IHetznerService.GetServerAsync), _ => _vm)
            .On(nameof(IHetznerService.CreateSshKeyAsync), _ => new HetznerSshKey { Id = ProjectKeyId })
            .On(nameof(IHetznerService.EnableRescueWithKeysAsync), _ => new HetznerActionResponse { RootPassword = _password });
        // Each reboot: port 22 goes away once, then answers (probe 1, 3, … closed).
        _ssh.PortOpen = n => n % 2 == 0;
        _ssh.LastScan = [HostKey];
        _ssh.SshAnswer = (_, _) => new CommandResult
        {
            ExitCode = 0, Output = $"{RescueCommands.HostKeysBegin}\n{HostKey} root@web-new\n{RescueCommands.HostKeysEnd}\n",
        };
    }

    public void Dispose() => _store.Dispose();

    private async Task<ServerConfig> RecordAsync(Action<ServerConfig>? edit = null)
    {
        var reference = $"cloudkey:web-new:{Guid.NewGuid():N}";
        await _store.Vault.SetSecretAsync(reference, "tok-" + Guid.NewGuid().ToString("N"));
        var record = new ServerConfig
        {
            Id = "web-new", Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = 22,
            CloudProvider = CloudProvider.Hetzner, CloudServerId = VmId, CloudKeyRef = reference,
        };
        edit?.Invoke(record);
        await _store.Servers.AddServerAsync(record);
        return _store.Servers.GetServer("web-new")!;
    }

    private HetznerRescueBootstrap Bootstrap(bool memoryBacked = true) => new(
        new TestServices(_hetzner, _store.Vault), _store.Servers, _ssh,
        new SshKeyLeases(_store.Servers, _store.Keys, NullLogger<SshKeyLeases>.Instance, _keyBase, _ => memoryBacked),
        _pins, OnboardingOptionsFor.Settings(), _log, _keyBase, _ => memoryBacked)
    {
        Delay = (_, _) => Task.CompletedTask,
    };

    private Task<CloudBootstrapResult> RunAsync(ServerConfig server, string? confirm = null, bool memoryBacked = true) =>
        Bootstrap(memoryBacked).PrepareAsync(server, new OnboardingOptions(BootstrapMethod.HetznerRescueKey, confirm),
            new Progress<OnboardingProgress>(), default);

    private List<string> Mutations() => _cloud.Calls.Where(c => c is not nameof(IHetznerService.GetServerAsync)).ToList();

    [Fact]
    public async Task Happy_path_runs_in_contract_order()
    {
        var result = await RunAsync(await RecordAsync());

        Assert.True(result.Success, result.Error);
        Assert.Equal([
            nameof(IHetznerService.CreateSshKeyAsync), nameof(IHetznerService.EnableRescueWithKeysAsync), nameof(IHetznerService.RebootAsync),
            nameof(IHetznerService.DisableRescueAsync), nameof(IHetznerService.RebootAsync), nameof(IHetznerService.DeleteSshKeyAsync),
        ], Mutations());
        Assert.Equal(["disk:web-new"], _pins.Calls);   // pinned from the disk before any contact with the real system
        var injected = FakeSshToolchain.FingerprintOf(_ssh.GeneratedPublicKey);
        Assert.Equal(injected, result.InjectedKeyFingerprint);
        var record = _store.Servers.GetServer("web-new")!;
        Assert.Equal(injected, record.BootstrapKeyInjectedFingerprint);
        Assert.True(record.BootstrapKeyGenerated);
        Assert.Equal(HetznerRescueBootstrap.BootstrapKeyFileName("web-new"), record.SshKeyFileName);
        Assert.False(result.StandingBootstrapKey);
        // The injected line carries the run's marker comment.
        Assert.Contains(RescueCommands.MarkerPrefix("web-new"), Decoded(Assert.Single(_ssh.SshCalls).Command));
    }

    /// <summary>The base64 values of a rescue script, decoded (every value reaches the shell only so).</summary>
    private static string Decoded(string script) => string.Join("\n", System.Text.RegularExpressions.Regex
        .Matches(script, "echo ([A-Za-z0-9+/=]+) \\| base64 -d").Select(m => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))));

    [Fact]
    public async Task Soft_reboot_that_does_not_come_back_falls_back_to_one_hard_reset()
    {
        // The soft reboot is never observed (3 polls open with the same keys); after the reset the port goes away and back.
        _ssh.PortOpen = n => n is not (4 or 6);

        var result = await RunAsync(await RecordAsync());

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, _cloud.Calls.Count(c => c == nameof(IHetznerService.ResetAsync)));
        Assert.Equal(2, _cloud.Calls.Count(c => c == nameof(IHetznerService.RebootAsync)));
    }

    [Fact]
    public async Task Reboot_counts_only_after_port_or_host_key_went_away()
    {
        // Port open throughout with the same keys: not a reboot — the one hard reset, and then the run fails.
        _ssh.PortOpen = _ => true;
        var stuck = await RunAsync(await RecordAsync());
        Assert.False(stuck.Success);
        // One reset as the fallback, and the clean-up's one (the VM may be in rescue whether or not it was observed).
        Assert.Equal(2, _cloud.Calls.Count(c => c == nameof(IHetznerService.ResetAsync)));
        Assert.Contains(nameof(IHetznerService.DisableRescueAsync), _cloud.Calls);
        Assert.Empty(_ssh.SshCalls);   // nothing ran in a "rescue" that was never observed
    }

    [Fact]
    public async Task A_changed_host_key_counts_as_a_reboot()
    {
        _ssh.PortOpen = _ => true;
        // before the first reboot, after it (rescue), before the second, after it (the real system again)
        foreach (var scan in new[] { HostKey, RescueKey, RescueKey, HostKey }) _ssh.Scans.Enqueue([scan]);

        var result = await RunAsync(await RecordAsync());

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(nameof(IHetznerService.ResetAsync), _cloud.Calls);
    }

    [Fact]
    public async Task Failure_after_enable_rescue_disables_rescue_resets_and_deletes_the_key()
    {
        _ssh.SshAnswer = (_, _) => new CommandResult { ExitCode = 3, Error = "no root partition" };

        var result = await RunAsync(await RecordAsync());

        Assert.False(result.Success);
        Assert.False(result.StandingBootstrapKey);   // nothing was injected
        var after = _cloud.Calls.SkipWhile(c => c != nameof(IHetznerService.EnableRescueWithKeysAsync)).ToList();
        Assert.Contains(nameof(IHetznerService.DeleteSshKeyAsync), after);
        Assert.Contains(nameof(IHetznerService.DisableRescueAsync), after);
        Assert.Equal(1, after.Count(c => c == nameof(IHetznerService.ResetAsync)));
        var record = _store.Servers.GetServer("web-new")!;
        Assert.Null(record.SshKeyFileName);          // the generated key is no access to anything: gone
        Assert.False(record.BootstrapKeyGenerated);
        Assert.Null(record.BootstrapKeyInjectedFingerprint);
    }

    public static TheoryData<string> NotFresh => ["pinned", "mtls"];

    [Theory]
    [MemberData(nameof(NotFresh))]
    public async Task Not_fresh_server_is_refused(string kind)
    {
        var server = await RecordAsync(r =>
        {
            if (kind == "pinned") r.SshHostKeyFingerprint = "SHA256:already";
            else r.TcpClientCertPath = "/app/data/mtls/clients/web-new/1/client.crt";
        });

        var result = await RunAsync(server);

        Assert.False(result.Success);
        Assert.Empty(Mutations());
        Assert.Empty(_ssh.GeneratedIn);
    }

    [Fact]
    public async Task Vm_with_another_ip_is_refused()
    {
        _vm.PublicNet!.Ipv4!.Ip = "203.0.113.99";

        var result = await RunAsync(await RecordAsync());

        Assert.False(result.Success);
        Assert.Empty(Mutations());
    }

    [Fact]
    public async Task Vm_name_differing_from_the_record_is_refused_without_confirmation()
    {
        _vm.Name = "other-vm";

        var result = await RunAsync(await RecordAsync());

        Assert.False(result.Success);
        Assert.Contains("other-vm", result.Error);
        Assert.Empty(Mutations());
    }

    [Fact]
    public async Task Confirmed_vm_name_must_equal_the_current_vm_name()
    {
        _vm.Name = "other-vm";
        var server = await RecordAsync();

        var stale = await RunAsync(server, confirm: "other-vm-before-rename");
        Assert.False(stale.Success);
        Assert.Empty(Mutations());

        var confirmed = await RunAsync(server, confirm: "other-vm");
        Assert.True(confirmed.Success, confirmed.Error);
    }

    [Fact]
    public async Task Rescue_host_key_never_reaches_the_main_known_hosts()
    {
        var result = await RunAsync(await RecordAsync());

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(_ssh.SshCalls).Call;
        var main = SshHostKeyPolicy.KnownHostsFile(_store.Paths);
        Assert.NotEqual(Path.GetFullPath(main), Path.GetFullPath(call.KnownHostsFile));
        Assert.False(File.Exists(call.KnownHostsFile));   // the run-scoped file is gone with the run
        Assert.False(File.Exists(main) && File.ReadAllText(main).Contains(FakeSshToolchain.KeyText('q'), StringComparison.Ordinal));
        Assert.Equal(("root", 22, "203.0.113.20"), (call.User, call.Port, call.Host));
    }

    [Fact]
    public async Task Rescue_password_is_not_returned_or_logged()
    {
        using var scope = MutationScope.Begin("server.onboard", "op-pw");

        var result = await RunAsync(await RecordAsync());

        Assert.True(result.Success, result.Error);
        var everything = string.Join("\n", [
            result.Error ?? "", result.InjectedKeyFingerprint ?? "", .. result.CleanupWarnings,
            .. scope.Results.Select(r => r.Details), .. _log.Lines,
            File.ReadAllText(Path.Combine(_store.Root, "servers.json")),
        ]);
        Assert.DoesNotContain(_password, everything, StringComparison.Ordinal);
        Assert.Null(_store.Servers.GetServer("web-new")!.SshPassword);
    }

    [Fact]
    public async Task Generated_key_is_written_only_to_a_memory_backed_directory()
    {
        // Without a memory-backed directory no key is generated and nothing at the provider changes.
        var refused = await RunAsync(await RecordAsync(), memoryBacked: false);
        Assert.False(refused.Success);
        Assert.Empty(_ssh.GeneratedIn);
        Assert.Empty(Mutations());

        var result = await RunAsync(_store.Servers.GetServer("web-new")!);
        Assert.True(result.Success, result.Error);
        var dir = Assert.Single(_ssh.GeneratedIn);
        Assert.Equal(Path.GetFullPath(_keyBase), Path.GetFullPath(Path.GetDirectoryName(dir)!));
        Assert.False(Directory.Exists(dir));          // the plaintext is gone once it is sealed
    }

    private Task<ServerConfig> InjectedEarlierAsync() => RecordAsync(r =>
    {
        r.BootstrapKeyInjectedFingerprint = FakeSshToolchain.FingerprintOf(_ssh.GeneratedPublicKey);
        r.BootstrapKeyGenerated = true;
    });

    [Fact]
    public async Task Resume_after_injection_before_pin_reuses_the_key()
    {
        await InjectedEarlierAsync();
        await _store.Servers.SaveSshKeyAsync("web-new", HetznerRescueBootstrap.BootstrapKeyFileName("web-new"),
            System.Text.Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));

        var resumed = await RunAsync(_store.Servers.GetServer("web-new")!);

        Assert.True(resumed.Success, resumed.Error);
        Assert.Empty(_ssh.GeneratedIn);   // nothing new generated: the key of the earlier run is used again
        Assert.Equal(FakeSshToolchain.FingerprintOf(_ssh.GeneratedPublicKey), resumed.InjectedKeyFingerprint);
        Assert.Equal(["disk:web-new"], _pins.Calls);
    }

    [Fact]
    public async Task Resume_after_injection_with_the_key_gone_is_refused_and_points_to_abandon()
    {
        var gone = await RunAsync(await InjectedEarlierAsync());

        Assert.False(gone.Success);
        Assert.Contains("abandon", gone.Error);
        Assert.Empty(Mutations());        // refused before any provider change
        Assert.Empty(_ssh.GeneratedIn);
    }

    private sealed class ListLogger : ILogger<HetznerRescueBootstrap>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
