using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 parts C and D: the onboarding pipeline's own steps. CloudBootstrap (when asked) before HostKey, the host
/// key pinned before any command, a mismatch stops there; BootstrapCleanup removes the bootstrap line (read back) before
/// the generated key goes; a run that fails while the line stands says so; abandon runs only the clean-up; the D9 auth
/// key reaches the host only through the secret channel.
/// </summary>
public sealed class OnboardingServiceStepTests
{
    internal const string Id = "neuhof";
    private static readonly string Removal = RescueCommands.RemoveMarkerLines(Id);
    private static readonly CommandResult Clean = new() { ExitCode = 0, Output = RescueCommands.Clean + "\n" };

    internal sealed class Lines : IProgress<OnboardingProgress>
    {
        public List<OnboardingProgress> All { get; } = [];
        public void Report(OnboardingProgress value) => All.Add(value);
    }

    /// <summary>A bootstrap that does what the rescue run leaves behind: the injected line and the pin from the disk.</summary>
    private sealed class FakeBootstrap(IServerConfigService servers, bool succeed = true) : ICloudBootstrap
    {
        public int Runs { get; private set; }
        public BootstrapMethod Method => BootstrapMethod.HetznerRescueKey;

        public async Task<CloudBootstrapResult> PrepareAsync(ServerConfig server, OnboardingOptions options,
            IProgress<OnboardingProgress> progress, CancellationToken ct)
        {
            Runs++;
            var record = servers.GetServer(server.Id)!.Clone();
            record.BootstrapKeyGenerated = true;
            record.BootstrapKeyInjectedFingerprint = "SHA256:injected";
            if (succeed)
            {
                record.SshHostKeyType = "ssh-ed25519";
                record.SshHostKeyFingerprint = AcceptingHostKeyPins.Fingerprint;
                record.SshHostKeySource = SshHostKeySource.RescueDisk;
                record.SshHostKeyPinnedAtUtc = DateTime.UtcNow;
            }
            await servers.UpdateServerAsync(record);
            return new CloudBootstrapResult(succeed, succeed ? null : "rescue failed", null, "SHA256:injected", !succeed, []);
        }
    }

    /// <summary>Forwards to a server store and logs the key and record operations in order.</summary>
    public class LoggingServers : DispatchProxy
    {
        private IServerConfigService _inner = null!;
        private List<string> _log = null!;

        public static IServerConfigService Over(IServerConfigService inner, List<string> log)
        {
            var proxy = Create<IServerConfigService, LoggingServers>();
            ((LoggingServers)(object)proxy)._inner = inner;
            ((LoggingServers)(object)proxy)._log = log;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is nameof(IServerConfigService.DeleteSshKeyAsync) or nameof(IServerConfigService.UpdateServerAsync))
                _log.Add(method.Name);
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
    }

    internal static OnboardingService Service(PerHostCredentialTests.OnboardingExecutor exec, IServerConfigService servers,
        CredentialWorld w, ISshHostKeyPins pins, ICloudBootstrap? bootstrap = null, IVaultService? vault = null,
        string? authKeyRef = null, IOnboardingRunRecorder? runs = null) =>
        new(exec, servers, PerHostCredentialTests.ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance, hostKeys: pins, bootstraps: bootstrap is null ? null : [bootstrap],
            vault: vault, settings: OnboardingOptionsFor.Settings(authKeyRef: authKeyRef), runs: runs)
        {
            ProbeCredential = (_, _) => Task.FromResult(MtlsProbeOutcome.Refused),
            VerifyDelay = TimeSpan.Zero,
        };

    internal static Task<OnboardingResult> RunAsync(OnboardingService service, CredentialWorld w, Lines lines,
        BootstrapMethod bootstrap = BootstrapMethod.None, bool abandon = false, bool mayReplace = false) =>
        service.OnboardServerAsync(Id, mayReplace, PerHostCredentialTests.Decided(w, Id),
            new OnboardingOptions(bootstrap, AbandonBootstrap: abandon), lines, default);

    /// <param name="pinnedFromDisk">The rescue run also pinned the host keys read from the disk (it failed later): a run
    /// without a bootstrap may then go on. Without the pin only a resume or an abandon may (see
    /// <see cref="Injected_without_a_pin_refuses_a_run_without_bootstrap"/>).</param>
    private static async Task InjectedAsync(CredentialWorld w, bool pinnedFromDisk = false)
    {
        var record = w.Server(Id).Clone();
        record.BootstrapKeyGenerated = true;
        record.BootstrapKeyInjectedFingerprint = "SHA256:injected";
        if (pinnedFromDisk)
        {
            record.SshHostKeyType = "ssh-ed25519";
            record.SshHostKeyFingerprint = AcceptingHostKeyPins.Fingerprint;
            record.SshHostKeySource = SshHostKeySource.RescueDisk;
            record.SshHostKeyPinnedAtUtc = DateTime.UtcNow;
        }
        await w.Servers.UpdateServerAsync(record);
    }

    /// <summary>Carried over from the PR 2 review: the service itself (not only the UI's <c>RescueResumeForced</c>)
    /// refuses a run with bootstrap <c>None</c> on a record that carries an injected bootstrap key and no host-key pin —
    /// otherwise MCP <c>onboard_server</c> could pin the (maybe still rescue) host on first contact. Nothing is pinned,
    /// no command runs, the line stays reported, and the answer points to the resume and to the abandon.</summary>
    [Fact]
    public async Task Injected_without_a_pin_refuses_a_run_without_bootstrap()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        await InjectedAsync(w);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");
        var pins = new AcceptingHostKeyPins();

        var result = await RunAsync(Service(exec, w.Servers, w, pins), w, new Lines());

        Assert.False(result.Success);
        Assert.Equal(OnboardingStep.HostKey, result.FailedStep);
        Assert.Empty(result.CompletedSteps);
        Assert.Contains("hetzner_rescue", result.Error);
        Assert.Contains("abandon", result.Error);
        Assert.Empty(pins.Calls);
        Assert.Empty(exec.Scripts);
        Assert.True(result.StandingBootstrapKey);
        Assert.Equal("SHA256:injected", w.Server(Id).BootstrapKeyInjectedFingerprint);
        Assert.Null(w.Server(Id).SshHostKeyFingerprint);
    }

    [Fact]
    public async Task Step_order_without_bootstrap()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins()), w, new Lines());

        Assert.True(result.Success, result.Error);
        Assert.Equal(OnboardingStep.HostKey, result.CompletedSteps[0]);
        Assert.Equal(OnboardingStep.Bootstrap, result.CompletedSteps[1]);
        Assert.Equal(OnboardingStep.BootstrapCleanup, result.CompletedSteps[^1]);
        Assert.DoesNotContain(OnboardingStep.CloudBootstrap, result.CompletedSteps);
    }

    [Fact]
    public async Task Step_order_with_bootstrap()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c => c == Removal ? Clean : null,
        };
        var pins = new AcceptingHostKeyPins();
        var bootstrap = new FakeBootstrap(w.Servers);

        var result = await RunAsync(Service(exec, w.Servers, w, pins, bootstrap), w, new Lines(), BootstrapMethod.HetznerRescueKey);

        Assert.True(result.Success, result.Error);
        Assert.Equal([OnboardingStep.CloudBootstrap, OnboardingStep.HostKey, OnboardingStep.Bootstrap], result.CompletedSteps.Take(3));
        Assert.Equal(OnboardingStep.BootstrapCleanup, result.CompletedSteps[^1]);
        Assert.Equal(["verify:" + Id], pins.Calls);   // the pin from the disk is verified, not a first contact
        Assert.Null(w.Server(Id).BootstrapKeyInjectedFingerprint);
        Assert.False(w.Server(Id).BootstrapKeyGenerated);
    }

    [Fact]
    public async Task Resume_skips_CloudBootstrap_once_pinned_from_disk()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        var record = await PerHostCredentialTests.SshRecordAsync(w, Id);
        record.SshHostKeyType = "ssh-ed25519";
        record.SshHostKeyFingerprint = AcceptingHostKeyPins.Fingerprint;
        record.SshHostKeySource = SshHostKeySource.RescueDisk;
        record.SshHostKeyPinnedAtUtc = DateTime.UtcNow;
        record.SshKeyFileName = "whiskers-bootstrap-" + Id;
        await w.Servers.UpdateServerAsync(record);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");
        var bootstrap = new FakeBootstrap(w.Servers);

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins(), bootstrap), w, new Lines(),
            BootstrapMethod.HetznerRescueKey);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, bootstrap.Runs);
    }

    [Fact]
    public async Task Host_key_is_pinned_before_any_command()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");
        var commandsAtPin = -1;
        var pins = new AcceptingHostKeyPins { BeforePin = () => commandsAtPin = exec.Scripts.Count };

        var result = await RunAsync(Service(exec, w.Servers, w, pins), w, new Lines());

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, commandsAtPin);
        Assert.NotEmpty(exec.Scripts);
    }

    [Fact]
    public async Task Without_a_pins_service_an_ssh_record_runs_no_command()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");
        var service = new OnboardingService(exec, w.Servers, PerHostCredentialTests.ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance);

        var result = await RunAsync(service, w, new Lines());

        Assert.Equal(OnboardingStep.HostKey, result.FailedStep);
        Assert.Empty(exec.Scripts);
    }

    [Fact]
    public async Task Mismatch_stops_at_HostKey_and_runs_no_command()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");
        var (pins, script) = ScriptedProxy<ISshHostKeyPins>.Create();
        script.On(nameof(ISshHostKeyPins.PinFirstContactAsync),
            _ => throw new SshHostKeyMismatchException("203.0.113.20", "ssh-ed25519", "SHA256:pinned", "SHA256:served"));

        var result = await RunAsync(Service(exec, w.Servers, w, pins), w, new Lines());

        Assert.False(result.Success);
        Assert.Equal(OnboardingStep.HostKey, result.FailedStep);
        Assert.Equal(("SHA256:pinned", "SHA256:served"), result.HostKeyMismatch);
        Assert.Empty(exec.Scripts);
    }

    [Fact]
    public async Task BootstrapCleanup_removes_the_injected_line_before_the_key()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        await InjectedAsync(w, pinnedFromDisk: true);
        var log = new List<string>();
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c =>
            {
                if (c != Removal) return null;
                log.Add("line-removed");
                return Clean;
            },
        };

        var result = await RunAsync(Service(exec, LoggingServers.Over(w.Servers, log), w, new AcceptingHostKeyPins()), w, new Lines());

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, exec.Scripts.Count(s => s == Removal));
        Assert.True(log.IndexOf("line-removed") < log.IndexOf(nameof(IServerConfigService.DeleteSshKeyAsync)));
        Assert.Null(w.Server(Id).BootstrapKeyInjectedFingerprint);
        Assert.False(w.Server(Id).BootstrapKeyGenerated);
        Assert.False(result.StandingBootstrapKey);
    }

    [Fact]
    public async Task A_supplied_key_is_deleted_without_a_removal_command()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins()), w, new Lines());

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(Removal, exec.Scripts);
    }

    [Fact]
    public async Task Failed_after_injection_reports_standing_key()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        await InjectedAsync(w, pinnedFromDisk: true);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c => c == "echo ok" ? new CommandResult { ExitCode = 255, Error = "Connection refused" } : null,
        };
        var lines = new Lines();
        var runs = new OnboardingRuns();
        using var scope = MutationScope.Begin("server.onboard", "op-s");

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins(), runs: runs), w, lines);

        Assert.False(result.Success);
        Assert.True(result.StandingBootstrapKey);
        var warning = BootstrapCleanup.StandingKeyWarning("SHA256:injected", w.Server(Id).Name);
        Assert.Contains(warning, result.CleanupWarnings);
        Assert.Contains(lines.All, l => l.Message.Contains(warning, StringComparison.Ordinal));
        Assert.Contains(scope.Results, r => r.Details.Contains("standingBootstrapKey: true", StringComparison.Ordinal));
        // Contract 0018 part E: the run snapshot (get_onboarding_status) carries it too.
        var snapshot = runs.Latest(Id);
        Assert.NotNull(snapshot);
        Assert.Equal("op-s", snapshot.OperationId);
        Assert.True(snapshot.Finished);
        Assert.True(snapshot.Result!.StandingBootstrapKey);
        Assert.Contains(snapshot.Log, l => l.Contains(warning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_failure_fails_the_run()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        await InjectedAsync(w, pinnedFromDisk: true);
        var log = new List<string>();
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c => c == Removal ? new CommandResult { ExitCode = 4, Error = "a bootstrap line is still in authorized_keys" } : null,
        };

        var result = await RunAsync(Service(exec, LoggingServers.Over(w.Servers, log), w, new AcceptingHostKeyPins()), w, new Lines());

        Assert.False(result.Success);
        Assert.Equal(OnboardingStep.BootstrapCleanup, result.FailedStep);
        Assert.True(result.StandingBootstrapKey);
        Assert.Equal("SHA256:injected", w.Server(Id).BootstrapKeyInjectedFingerprint);   // kept for a resume or abandon
        Assert.True(w.Server(Id).BootstrapKeyGenerated);
        Assert.DoesNotContain(nameof(IServerConfigService.DeleteSshKeyAsync), log);          // the key is kept
    }

    [Fact]
    public async Task Abandon_runs_only_BootstrapCleanup()
    {
        // The record has an mTLS host shell (the world's TCP record): the removal runs there, nothing else does.
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await InjectedAsync(w);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20") { Answer = c => c == Removal ? Clean : null };

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins()), w, new Lines(), abandon: true, mayReplace: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal([OnboardingStep.BootstrapCleanup], result.CompletedSteps);
        Assert.Equal([Removal], exec.Scripts);
        Assert.Null(w.Server(Id).BootstrapKeyInjectedFingerprint);
    }

    /// <summary>Review r1 MEDIUM 2 / r2 (INV-63): an abandon over SSH verifies a known host key; with none known (injected,
    /// never pinned) it refuses and points to the resume; a different key or no pins service sends no command at all.</summary>
    [Theory]
    [InlineData("known")]
    [InlineData("first")]
    [InlineData("mismatch")]
    [InlineData("no-service")]
    public async Task Abandon_over_ssh_checks_the_host_key_before_its_command(string kind)
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        var ssh = await PerHostCredentialTests.SshRecordAsync(w, Id);
        ssh.SshKeyFileName = HetznerRescueBootstrap.BootstrapKeyFileName(Id);
        await w.Servers.UpdateServerAsync(ssh);
        w.Servers.KeyPath = s => s.SshKeyFileName is null ? null : "/data/ssh-keys/" + s.SshKeyFileName;   // the sealed bootstrap key is there
        await InjectedAsync(w);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20") { Answer = c => c == Removal ? Clean : null };
        var pins = new AcceptingHostKeyPins();
        var commandsAtCheck = -1;
        pins.BeforePin = () => commandsAtCheck = exec.Scripts.Count;
        if (kind is "known" or "mismatch")
            pins.KnownHostsPin = new SshHostKeyPin("ssh-ed25519", "SHA256:known", SshHostKeySource.FirstContact, null, null);
        if (kind == "mismatch")
            pins.VerifyFails = new SshHostKeyMismatchException("100.64.0.20", "ssh-ed25519", "SHA256:known", "SHA256:other");

        var result = await RunAsync(Service(exec, w.Servers, w, kind == "no-service" ? null! : pins), w, new Lines(), abandon: true);

        switch (kind)
        {
            case "known":
                Assert.True(result.Success, result.Error);
                Assert.Equal(["verify:" + Id], pins.Calls);
                Assert.Equal([Removal], exec.Scripts);
                break;
            case "first":
                // Review r2: injected but never pinned — the VM may still be in rescue. No first contact (it could trust
                // the rescue system's key); the person is pointed to the resume, which pins from the disk.
                Assert.False(result.Success);
                Assert.Contains("Resume", result.Error);
                Assert.Empty(pins.Calls);
                Assert.Equal(-1, commandsAtCheck);
                Assert.Empty(exec.Scripts);
                Assert.Equal("SHA256:injected", w.Server(Id).BootstrapKeyInjectedFingerprint);
                break;
            default:
                Assert.False(result.Success);
                Assert.Empty(exec.Scripts);
                Assert.Equal("SHA256:injected", w.Server(Id).BootstrapKeyInjectedFingerprint);   // still reported
                break;
        }
    }

    [Fact]
    public async Task Abandon_without_a_way_to_the_host_records_the_dead_line()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);   // SSH without a usable key
        await InjectedAsync(w);
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20");

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins()), w, new Lines(), abandon: true);

        Assert.True(result.Success, result.Error);
        Assert.Empty(exec.Scripts);
        Assert.Contains(result.CleanupWarnings, x => x.Contains("SHA256:injected", StringComparison.Ordinal));
        Assert.Null(w.Server(Id).BootstrapKeyInjectedFingerprint);
    }

    [Fact]
    public async Task Tailscale_auth_key_reaches_the_host_only_through_the_secret_channel()
    {
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        using var store = new TempServerStore();
        var authKey = "tskey-auth-" + Guid.NewGuid().ToString("N");
        await store.Vault.SetSecretAsync("onboarding:tsauthkey:t1", authKey);
        var running = false;
        string? provided = null;
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c =>
            {
                if (c.Contains("--auth-key=file:", StringComparison.Ordinal))
                {
                    provided = HostCommandSecrets.Current.FirstOrDefault(s => s.Key == "WHISKERS_TS_AUTHKEY").Value;
                    running = true;
                    return new CommandResult { ExitCode = 0 };
                }
                return c.Contains("BackendState", StringComparison.Ordinal) && !running ? new CommandResult { ExitCode = 0, Output = "" } : null;
            },
        };
        var lines = new Lines();
        using var scope = MutationScope.Begin("server.onboard", "op-ts");

        var result = await RunAsync(Service(exec, w.Servers, w, new AcceptingHostKeyPins(), vault: store.Vault,
            authKeyRef: "onboarding:tsauthkey:t1"), w, lines);

        Assert.True(result.Success, result.Error);
        Assert.Equal(authKey, provided);
        Assert.All(exec.Scripts, s => Assert.DoesNotContain(authKey, s, StringComparison.Ordinal));
        Assert.All(lines.All, l => Assert.DoesNotContain(authKey, l.Message + l.TailscaleLoginUrl, StringComparison.Ordinal));
        Assert.All(scope.Results, r => Assert.DoesNotContain(authKey, r.Details, StringComparison.Ordinal));
        Assert.DoesNotContain(lines.All, l => l.TailscaleLoginUrl is not null);   // enrolled without a login link
    }
}
