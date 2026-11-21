using System.Diagnostics;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 (INV-64): removing a server whose host still carries a bootstrap line runs the fixed removal command
/// first, within its 30-second bound, before the record goes; an unreachable host does not stop the removal — the
/// sealed key goes with the record and the dead line is named. A cloud credential shared by reference stays while
/// another record names it.
/// </summary>
public sealed class ServerRemovalTests : IDisposable
{
    private readonly TempServerStore _store = new();

    public void Dispose() => _store.Dispose();

    private sealed class Executor(Func<string, CancellationToken, Task<CommandResult>> answer) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return answer(command, ct);
        }
    }

    private async Task<ServerConfig> StandingAsync(string id = "web-new", string? cloudRef = null)
    {
        await _store.Servers.AddServerAsync(new ServerConfig
        {
            Id = id, Name = id, ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = 22,
            BootstrapKeyGenerated = true, BootstrapKeyInjectedFingerprint = "SHA256:injected",
            CloudProvider = cloudRef is null ? CloudProvider.None : CloudProvider.Hetzner, CloudKeyRef = cloudRef,
        });
        await _store.Servers.SaveSshKeyAsync(id, HetznerRescueBootstrap.BootstrapKeyFileName(id),
            System.Text.Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));
        return _store.Servers.GetServer(id)!;
    }

    /// <summary>A host whose key Whiskers knows (a known_hosts line), so the removal may talk SSH to it after verifying.</summary>
    private readonly AcceptingHostKeyPins _pins = new()
    {
        KnownHostsPin = new SshHostKeyPin("ssh-ed25519", "SHA256:known", SshHostKeySource.FirstContact, null, null),
    };

    private BootstrapCleanup Cleanup(IHostCommandExecutor exec) => new(exec, _pins);

    /// <summary>Review r1 MEDIUM 2 (INV-63): the removal verifies the host key before its SSH command; with no pins
    /// service, no pin or a different key it sends nothing, names the dead line and still removes the record and key.</summary>
    [Theory]
    [InlineData("no-service")]
    [InlineData("no-pin")]
    [InlineData("mismatch")]
    public async Task Remove_of_an_ssh_host_without_a_verified_key_sends_no_command_and_names_the_dead_line(string kind)
    {
        var server = await StandingAsync();
        var exec = new Executor((_, _) => Task.FromResult(new CommandResult { ExitCode = 0, Output = RescueCommands.Clean }));
        var pins = new AcceptingHostKeyPins();
        if (kind == "mismatch")
        {
            pins.KnownHostsPin = _pins.KnownHostsPin;
            pins.VerifyFails = new SshHostKeyMismatchException("203.0.113.20", "ssh-ed25519", "SHA256:known", "SHA256:other");
        }
        using var scope = MutationScope.Begin("server.remove", "op-rm-pin");

        var result = await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, server, null,
            new BootstrapCleanup(exec, kind == "no-service" ? null : pins));

        Assert.Empty(exec.Commands);
        Assert.Equal(BootstrapCleanup.DeadLineWarning("SHA256:injected", "203.0.113.20"), result.StandingKeyWarning);
        Assert.Null(_store.Servers.GetServer("web-new"));
        Assert.False(Directory.EnumerateFiles(_store.Paths.SshKeysDir, HetznerRescueBootstrap.BootstrapKeyFileName("web-new") + "*").Any());
    }

    /// <summary>Review r1 LOW 6: whatever the clean-up throws, the removal goes on and names the dead line.</summary>
    [Fact]
    public async Task Remove_goes_on_whatever_the_cleanup_throws()
    {
        var server = await StandingAsync();
        var (cleanup, script) = ScriptedProxy<IBootstrapCleanup>.Create();
        script.On(nameof(IBootstrapCleanup.RemoveMarkerLinesAsync), _ => throw new System.Text.Json.JsonException("unexpected"));

        var result = await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, server, null, cleanup);

        Assert.Equal(BootstrapCleanup.DeadLineWarning("SHA256:injected", "203.0.113.20"), result.StandingKeyWarning);
        Assert.Null(_store.Servers.GetServer("web-new"));
    }

    private string KeyPath(string id) => Path.Combine(_store.Paths.SshKeysDir, HetznerRescueBootstrap.BootstrapKeyFileName(id));

    [Fact]
    public async Task Remove_takes_the_standing_bootstrap_line_and_key()
    {
        var server = await StandingAsync();
        var exec = new Executor((_, _) => Task.FromResult(new CommandResult { ExitCode = 0, Output = RescueCommands.Clean }));
        using var scope = MutationScope.Begin("server.remove", "op-rm");

        var result = await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, server, null, Cleanup(exec));

        Assert.Equal([RescueCommands.RemoveMarkerLines("web-new")], exec.Commands);
        Assert.Null(result.StandingKeyWarning);
        Assert.Null(_store.Servers.GetServer("web-new"));
        Assert.False(Directory.EnumerateFiles(_store.Paths.SshKeysDir, HetznerRescueBootstrap.BootstrapKeyFileName("web-new") + "*").Any());
        Assert.Contains(scope.Results, r => r.Details.Contains("SHA256:injected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_of_an_unreachable_host_deletes_the_key_and_names_the_dead_line()
    {
        var server = await StandingAsync();
        var exec = new Executor((_, _) => Task.FromResult(new CommandResult { ExitCode = 255, Error = "Connection refused" }));
        using var scope = MutationScope.Begin("server.remove", "op-rm2");

        var result = await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, server, null, Cleanup(exec));

        Assert.Equal(BootstrapCleanup.DeadLineWarning("SHA256:injected", "203.0.113.20"), result.StandingKeyWarning);
        Assert.Null(_store.Servers.GetServer("web-new"));
        Assert.False(Directory.EnumerateFiles(_store.Paths.SshKeysDir, HetznerRescueBootstrap.BootstrapKeyFileName("web-new") + "*").Any());
        Assert.Contains(scope.Results, r => r.Details.Contains("standingBootstrapKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_runs_the_cleanup_before_the_record_goes_and_within_its_bound()
    {
        var server = await StandingAsync();
        var recordThere = false;
        var exec = new Executor(async (_, ct) =>
        {
            recordThere = _store.Servers.GetServer("web-new") is not null;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);   // a host that never answers
            return new CommandResult { ExitCode = 0 };
        });
        Assert.Equal(TimeSpan.FromSeconds(30), BootstrapCleanup.Bound);
        // The bound is the cleanup's own: a token cancelled at once stands in for the 30 s elapsing.
        var cleanup = Cleanup(exec);
        var watch = Stopwatch.StartNew();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var direct = await cleanup.RemoveMarkerLinesAsync(server, cancelled.Token);

        Assert.False(direct.Removed);
        Assert.True(recordThere);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));

        // Through the removal: the command ran while the record still existed, and the removal went on afterwards.
        recordThere = false;
        var quick = new Executor((_, _) =>
        {
            recordThere = _store.Servers.GetServer("web-new") is not null;
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = RescueCommands.Clean });
        });
        await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, server, null, Cleanup(quick));
        Assert.True(recordThere);
        Assert.Null(_store.Servers.GetServer("web-new"));
    }

    [Fact]
    public async Task A_record_without_a_bootstrap_line_runs_no_host_command()
    {
        await _store.Servers.AddServerAsync(new ServerConfig { Id = "plain", Name = "plain", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.21" });
        var exec = new Executor((_, _) => Task.FromResult(new CommandResult { ExitCode = 0 }));

        var result = await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, _store.Servers.GetServer("plain")!, null,
            Cleanup(exec));

        Assert.Empty(exec.Commands);
        Assert.Null(result.StandingKeyWarning);
    }

    [Fact]
    public async Task The_shared_cloud_reference_survives_removal_of_one_record()
    {
        var reference = $"cloudkey:shared:{Guid.NewGuid():N}";
        await _store.Vault.SetSecretAsync(reference, "tok-" + Guid.NewGuid().ToString("N"));
        await _store.Servers.AddServerAsync(new ServerConfig
        {
            Id = "a", Name = "a", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.30", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = reference,
        });
        await _store.Servers.AddServerAsync(new ServerConfig
        {
            Id = "b", Name = "b", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.31", CloudProvider = CloudProvider.Hetzner, CloudKeyRef = reference,
        });

        await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, _store.Servers.GetServer("a")!);
        Assert.NotNull(_store.Vault.GetSecret(reference));

        await ServerRemoval.RemoveWithReportAsync(_store.Servers, _store.Vault, _store.Servers.GetServer("b")!);
        Assert.Null(_store.Vault.GetSecret(reference));
    }
}
