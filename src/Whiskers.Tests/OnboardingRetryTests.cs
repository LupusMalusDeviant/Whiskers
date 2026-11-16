using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Docker;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 A5 (R1, R4): every onboarding run states, before its first command, the SSH destination and the
/// credential the bootstrap will use — the key file name or "Passwort", never a value — from the record as it is at
/// that run. A retry reads the record again, so a credential changed between runs shows (and is used) at once.
/// </summary>
public class OnboardingRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-0018-retry-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfigService _servers;
    private readonly RefusingHost _host;
    private readonly OnboardingService _onboarding;

    public OnboardingRetryTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"), dataPaths: paths,
            sealedKeys: new SealedKeyFiles(TestVault.Usable, paths));
        _servers.InitializeAsync().GetAwaiter().GetResult();
        _host = new RefusingHost(_servers);
        _onboarding = new OnboardingService(_host, _servers, NullProxy<IDockerService>.Create(),
            NullProxy<IMtlsCertificateService>.Create(), EmptyClientCredentialStore(), NullLogger<OnboardingService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A transient bootstrap value as the form hands it over; made per run, never a literal.</summary>
    private static string HeldValue() => "held-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Retry_reads_the_record_again()
    {
        await _servers.AddServerAsync(new ServerConfig
        {
            Id = "neuhof", Name = "neuhof", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.7", SshUser = "root",
            SshPort = 22, SshPassword = HeldValue(),
        });

        var first = await RunAsync("neuhof");
        Assert.Equal(OnboardingStep.Bootstrap, first.Result.FailedStep);
        Assert.Contains("🔑 Bootstrap: ssh root@203.0.113.7:22 mit Passwort", first.Lines);
        Assert.Equal([true], _host.SawPassword);

        // Between the runs the user fixes the record: a key replaces the password, and the port changes.
        var fixedRecord = _servers.GetServer("neuhof")!.Clone();
        fixedRecord.SshPort = 2222;
        await _servers.UpdateServerAsync(fixedRecord);
        await _servers.SaveSshKeyAsync("neuhof", "id_neuhof", Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));

        var retry = await RunAsync("neuhof");
        Assert.Equal(OnboardingStep.Bootstrap, retry.Result.FailedStep);
        Assert.Contains("🔑 Bootstrap: ssh root@203.0.113.7:2222 mit Schlüsseldatei id_neuhof", retry.Lines);
        Assert.DoesNotContain(retry.Lines, l => l.Contains("Passwort", StringComparison.Ordinal) && l.StartsWith("🔑", StringComparison.Ordinal));
        Assert.Equal([true, false], _host.SawPassword);   // the retry's command ran with the key, not a stale password

        // The line comes before the first command of the run.
        var line = retry.Lines.FindIndex(l => l.StartsWith("🔑", StringComparison.Ordinal));
        Assert.True(line >= 0 && line < retry.Lines.FindIndex(l => l.StartsWith("❌", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Bootstrap_line_names_the_credential_not_its_value()
    {
        var held = HeldValue();
        var pem = SealedKeyFilesTests.NewPem();
        await _servers.AddServerAsync(new ServerConfig
        {
            Id = "neuhof", Name = "neuhof", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.7", SshUser = "root",
            SshPort = 22,
        });
        await _servers.SaveSshKeyAsync("neuhof", "id_neuhof", Encoding.UTF8.GetBytes(pem));
        var withValue = _servers.GetServer("neuhof")!.Clone();
        withValue.SshPassword = held;   // a password typed after the key: it wins in the executor
        await _servers.UpdateServerAsync(withValue);

        var run = await RunAsync("neuhof");

        Assert.Contains("🔑 Bootstrap: ssh root@203.0.113.7:22 mit Passwort", run.Lines);
        var everything = string.Join("\n", run.Lines.Append(run.Result.Error ?? ""));
        Assert.DoesNotContain(held, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(held[5..13], everything, StringComparison.Ordinal);   // nor a part of it
        Assert.DoesNotContain("PRIVATE KEY", everything, StringComparison.Ordinal);

        // Without the password the line names the key file, and says when that file is missing.
        var keyOnly = _servers.GetServer("neuhof")!.Clone();
        keyOnly.SshPassword = null;
        await _servers.UpdateServerAsync(keyOnly);
        Assert.Contains("🔑 Bootstrap: ssh root@203.0.113.7:22 mit Schlüsseldatei id_neuhof", (await RunAsync("neuhof")).Lines);

        var missing = _servers.GetServer("neuhof")!.Clone();
        missing.SshKeyFileName = "id_gone";
        await _servers.UpdateServerAsync(missing);
        Assert.Contains("🔑 Bootstrap: ssh root@203.0.113.7:22 mit Schlüsseldatei id_gone (fehlt)", (await RunAsync("neuhof")).Lines);
    }

    private async Task<(OnboardingResult Result, List<string> Lines)> RunAsync(string id)
    {
        var lines = new Lines();
        var decided = MtlsOnboardingState.ConnectionOf(_servers.GetServer(id)!);
        var result = await _onboarding.OnboardServerAsync(id, false, decided, lines, default);
        return (result, lines.All);
    }

    /// <summary>Collects progress synchronously (<see cref="Progress{T}"/> would post to the thread pool).</summary>
    private sealed class Lines : IProgress<string>
    {
        public List<string> All { get; } = [];
        public void Report(string value) { lock (All) All.Add(value); }
    }

    /// <summary>A host that refuses the bootstrap like a wrong credential would, and notes per command whether the
    /// record it would run with carries a password (the executor reads the record per command).</summary>
    private sealed class RefusingHost(IServerConfigService servers) : IHostCommandExecutor
    {
        public List<bool> SawPassword { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            SawPassword.Add(!string.IsNullOrEmpty(servers.GetServer(serverId)?.SshPassword));
            return Task.FromResult(new CommandResult { ExitCode = 255, Error = "Permission denied (publickey,password)." });
        }
    }
}
