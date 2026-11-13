using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>A server registry whose saves stick (the rollout switches the record), with a hook to fail a save.</summary>
internal sealed class MutableServerConfig(params ServerConfig[] servers) : IServerConfigService
{
    private readonly List<ServerConfig> _servers = servers.ToList();
    public int Saves { get; private set; }
    /// <summary>Called before a save; throw from it to model a crash at that point.</summary>
    public Action<ServerConfig>? BeforeSave { get; set; }

    public bool IsInitialized => true;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public List<ServerConfig> GetServers() => _servers;
    public List<ServerConfig> GetEnabledServers() => _servers.Where(s => s.Enabled).ToList();
    public ServerConfig? GetServer(string serverId) => _servers.FirstOrDefault(s => s.Id == serverId);
    public ServerConfig? GetDefaultServer() => _servers.FirstOrDefault();
    public bool SupportsTerminal(string? serverId) => false;
    public Task AddServerAsync(ServerConfig server) { _servers.Add(server.Clone()); return Task.CompletedTask; }
    public Task UpdateServerAsync(ServerConfig server)
    {
        BeforeSave?.Invoke(server);
        var i = _servers.FindIndex(s => s.Id == server.Id);
        if (i < 0) throw new InvalidOperationException("unknown server");
        _servers[i] = server.Clone();
        Saves++;
        return Task.CompletedTask;
    }
    public Task RemoveServerAsync(string serverId) { _servers.RemoveAll(s => s.Id == serverId); return Task.CompletedTask; }
    public Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData) => Task.CompletedTask;
    public string? GetSshKeyPath(ServerConfig server) => null;
    public Task DeleteSshKeyAsync(string serverId) => Task.CompletedTask;
    public Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef) => Task.FromResult(false);
}

/// <summary>
/// One host's proxy as the rollout sees it: ghostunnel and socket-proxy from one compose file, a running allow-list, the
/// compose file's allow-list, the guard timers per run and the markers. It answers the scripts of
/// <see cref="MtlsAllowListCommands"/> by what they do and admits a client credential exactly like ghostunnel would:
/// signed by the fleet CA and its common name on the running allow-list.
/// </summary>
internal sealed class SimulatedProxyHost(TestCa ca, string ip)
{
    public string Ip { get; } = ip;
    public List<string> RunningAllow { get; set; } = [MtlsCertificateService.SharedClientCommonName];
    public List<string> FileAllow { get; set; } = [MtlsCertificateService.SharedClientCommonName];
    /// <summary>Guard units of OTHER runs that are active on the host.</summary>
    public List<string> ForeignUnits { get; } = new();
    /// <summary>The run → timer state ("waiting", "dead", "elapsed", "not-found").</summary>
    public Dictionary<string, string> Timers { get; } = new();
    public HashSet<string> Markers { get; } = new();
    public List<string> Log { get; } = new();
    public bool Unreachable { get; set; }
    /// <summary>The ghostunnel container exists but does not run.</summary>
    public bool GhostunnelStopped { get; set; }
    /// <summary>The file pins another ghostunnel image than the one running.</summary>
    public bool ComposeDiffers { get; set; }
    /// <summary>The probe of this common name gives no TLS alert (a reset / timeout).</summary>
    public string? NotCheckedFor { get; set; }
    /// <summary>Runs right before the confirmation script (e.g. the guard fires).</summary>
    public Action? BeforeConfirm { get; set; }
    /// <summary>Runs right after a successful confirmation.</summary>
    public Action? AfterConfirm { get; set; }
    /// <summary>The Apply step does not take effect on the running container.</summary>
    public bool ApplyIgnored { get; set; }
    /// <summary>After each apply, this many state reads return nothing usable (ghostunnel half-recreated).</summary>
    public int UnusableReadsAfterApply { get; set; }
    /// <summary>The confirmation stops the timer and then fails (e.g. <c>touch</c> on a full disk).</summary>
    public bool ConfirmFailsAfterStop { get; set; }
    private int _unusableReads;

    private List<string>? _prev;
    private string? _pendingRun;

    public static readonly string[] ApprovedArgs = [.. MtlsAllowListCommands.ApprovedArgsWithoutAllowCn];

    /// <summary>The guard of the pending run fires now: no marker → the file is restored and ghostunnel recreated.</summary>
    public void FireGuard()
    {
        if (_pendingRun is not { } run) return;
        if (!Markers.Remove(run) && _prev is not null)
        {
            FileAllow = _prev;
            RunningAllow = [.. _prev];
            _prev = null;
        }
        Timers[run] = "dead";
        Log.Add("guard-fired:" + run);
    }

    /// <summary>The next this many host commands fail as unreachable (a brief outage).</summary>
    public int UnreachableNextCommands { get; set; }

    public CommandResult Execute(string script)
    {
        if (Unreachable) throw new HttpRequestException("host unreachable");
        if (UnreachableNextCommands > 0)
        {
            UnreachableNextCommands--;
            Log.Add("unreachable");
            throw new HttpRequestException("host briefly unreachable");
        }
        if (script.Contains("ghostunneldigests=", StringComparison.Ordinal))
        {
            if (_unusableReads > 0)
            {
                _unusableReads--;
                Log.Add("read-unusable");
                return Ok("");
            }
            Log.Add("read");
            return Ok(StateOutput());
        }
        if (script.Contains("echo applied", StringComparison.Ordinal))
        {
            // As the script: a .prev left by another run while a guard is armed is never overwritten.
            if (_prev is not null && ArmedUnits().Any())
            {
                Log.Add("apply-refused");
                return new CommandResult { ExitCode = 1, Error = "another run left the .prev while a guard is armed" };
            }
            return Ok(Apply(script));
        }
        if (script.Contains("systemctl list-units", StringComparison.Ordinal))
        {
            Log.Add("guards");
            return Ok(string.Join('\n', ArmedUnits()));
        }
        if (script.Contains("echo cleaned", StringComparison.Ordinal))
        {
            var run = Regex.Match(script, @"\.allow-ok-([a-z0-9]+)").Groups[1].Value;
            Markers.Remove(run);
            Log.Add("cleanup:" + run);
            return Ok("cleaned");
        }
        if (script.Contains("{{.State.Running}} {{json .Args}}", StringComparison.Ordinal))
        {
            Log.Add("args");
            return Ok((GhostunnelStopped ? "false " : "true ") + JsonSerializer.Serialize(Args(RunningAllow)));
        }
        if (script.Trim() == "echo ok") return Ok("ok");
        Log.Add("unexpected:" + script);
        return new CommandResult { ExitCode = 1, Error = "unexpected script" };
    }

    private string Apply(string script)
    {
        var cns = Regex.Match(script, "cns='([a-z0-9 .-]+)'").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var run = Regex.Match(script, @"--unit=whiskers-allow-guard-([a-z0-9]+) ").Groups[1].Value;
        Log.Add($"apply:{run}:{string.Join(",", cns)}");
        Assert.Contains("rm -f /opt/dockerproxy/.allow-ok-" + run + "\n", script.Replace("\r\n", "\n"));
        Markers.Remove(run);   // only this run's marker
        _prev = [.. FileAllow];
        _pendingRun = run;
        Timers[run] = "waiting";
        FileAllow = cns;
        if (!ApplyIgnored) RunningAllow = [.. cns];
        _unusableReads = UnusableReadsAfterApply;
        return "applied";
    }

    /// <summary>The confirmation script, as the host runs it (amendments 4–6).</summary>
    public (string Output, string Error, int ExitCode) Confirm(string script)
    {
        Assert.Contains("| timeout -k 5 20 flock -w 30 /opt/dockerproxy/.migrate-", script);
        var body = LockedBody(script);
        var run = Regex.Match(body, @"whiskers-allow-guard-([a-z0-9]+)\.timer").Groups[1].Value;
        BeforeConfirm?.Invoke();
        Log.Add("confirm:" + run);
        if (HangInConfirm) return ("", "the confirmation did not finish within 20 seconds inside the lock", 124);
        if (!Timers.TryGetValue(run, out var state) || state != "waiting")
            return ("", "the guard timer of this run is no longer waiting", 1);
        if (_prev is null) return ("", "the previous allow-list file is gone (the guard already restored it?)", 1);
        Timers[run] = "stopped";
        if (ConfirmFailsAfterStop) return ("", "the marker could not be written", MtlsAllowListCommands.ConfirmFailedAfterStop);
        Markers.Add(run);
        _prev = null;
        AfterConfirm?.Invoke();
        return ("confirmed", "", 0);
    }

    /// <summary>The confirmation's locked section hangs and is killed by <c>timeout 20</c> (nothing changed on the host).</summary>
    public bool HangInConfirm { get; set; }

    /// <summary>The section <see cref="MtlsAllowListCommands.Confirm"/> runs under the lock (base64 inside the script).</summary>
    public static string LockedBody(string confirmScript) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(
            Regex.Match(confirmScript, @"echo (\S+) \| base64 -d \| timeout").Groups[1].Value));

    /// <summary>How the credential a client presents is built from a record's paths (contract 0015: the key is sealed at
    /// rest; the world sets the app's loader). Null: plain PEM files.</summary>
    public Func<ServerConfig, X509Certificate2>? Loader { get; set; }

    /// <summary>ghostunnel's decision for a client credential.</summary>
    public MtlsProbeOutcome Probe(ServerConfig server) => Probe(server, simulateNoAlert: true);

    /// <summary>Whether the host admits the credential at all (the connection Whiskers itself runs over).</summary>
    public bool Admits(ServerConfig server) => Probe(server, simulateNoAlert: false) == MtlsProbeOutcome.Admitted;

    private MtlsProbeOutcome Probe(ServerConfig server, bool simulateNoAlert)
    {
        if (Unreachable) return MtlsProbeOutcome.NotChecked;
        X509Certificate2 cert;
        try
        {
            cert = Loader is { } load ? load(server) : X509Certificate2.CreateFromPemFile(server.TcpClientCertPath!, server.TcpClientKeyPath);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return MtlsProbeOutcome.NotChecked;
        }
        using (cert)
        {
            var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (simulateNoAlert && cn == NotCheckedFor) return MtlsProbeOutcome.NotChecked;
            var signedByFleet = IssuedBy(cert, ca);
            return signedByFleet && RunningAllow.Contains(cn) ? MtlsProbeOutcome.Admitted : MtlsProbeOutcome.Refused;
        }
    }

    private static bool IssuedBy(X509Certificate2 leaf, TestCa fleet)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.Add(fleet.Intermediate);
        chain.ChainPolicy.CustomTrustStore.Add(fleet.Root);
        return chain.Build(leaf);
    }

    private IEnumerable<string> ArmedUnits() =>
        ForeignUnits.Concat(Timers.Where(t => t.Value == "waiting").Select(t => MtlsAllowListCommands.GuardUnitPrefix + t.Key + ".timer"));

    private static List<string> Args(IEnumerable<string> allow) =>
        [.. ApprovedArgs, .. allow.Select(n => MtlsAllowListCommands.AllowCnFlag + n)];

    public string ComposeText()
    {
        var single = OnboardingCommands.DockerProxyCompose(Ip, "placeholder");
        var lines = string.Concat(FileAllow.Select(n => "      - --allow-cn=" + n + "\n"));
        var text = single.Replace("      - --allow-cn=placeholder\n", lines, StringComparison.Ordinal);
        return ComposeDiffers ? text.Replace("@sha256:65c4", "@sha256:0000", StringComparison.Ordinal) : text;
    }

    private string StateOutput()
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "dockerproxy",
            ["com.docker.compose.project.working_dir"] = "/opt/dockerproxy",
            ["com.docker.compose.project.config_files"] = "/opt/dockerproxy/docker-compose.yml",
        };
        var gu = new[]
        {
            new
            {
                Args = Args(RunningAllow),
                Config = new
                {
                    Image = "ghostunnel/ghostunnel:v1.11.5@sha256:65c4e34ec0ee0f30ea1f72b7411ec70a91c32fc50f067a8466ad2adb1e7420f2",
                    Env = new[] { "PATH=/usr/bin" },
                    Labels = new Dictionary<string, string>(labels) { ["com.docker.compose.service"] = "ghostunnel" },
                },
                HostConfig = new { PortBindings = new Dictionary<string, object[]> { ["2376/tcp"] = [new { HostIp = Ip, HostPort = "2376" }] } },
                Mounts = new[] { new { Source = "/opt/dockerproxy/certs", Destination = "/certs", RW = false } },
            },
        };
        var sp = new[]
        {
            new
            {
                Args = Array.Empty<string>(),
                Config = new
                {
                    Image = "tecnativa/docker-socket-proxy:v0.5.0",
                    Env = new[]
                    {
                        "CONTAINERS=1", "IMAGES=1", "NETWORKS=1", "VOLUMES=1", "INFO=1", "VERSION=1", "POST=1", "EXEC=0", "SWARM=0",
                        "SECRETS=0", "CONFIGS=0", "PLUGINS=0", "SYSTEM=0", "AUTH=0", "PATH=/usr/bin",
                    },
                    Labels = new Dictionary<string, string>(labels) { ["com.docker.compose.service"] = "socket-proxy" },
                },
                HostConfig = new { PortBindings = new Dictionary<string, object[]>() },
                Mounts = new[] { new { Source = "/var/run/docker.sock", Destination = "/var/run/docker.sock", RW = false } },
            },
        };
        var units = ArmedUnits();
        return string.Join('\n',
            "ghostunnel=" + B64(JsonSerializer.Serialize(gu)),
            "socketproxy=" + B64(JsonSerializer.Serialize(sp)),
            "ghostunneldigests=" + B64("[\"ghostunnel/ghostunnel@sha256:65c4e34ec0ee0f30ea1f72b7411ec70a91c32fc50f067a8466ad2adb1e7420f2\"]"),
            "socketproxydigests=" + B64("[\"tecnativa/docker-socket-proxy@sha256:1f5038b54f06c3e18422902cf00ba21803d1c97805aae032e5e6673d532d3459\"]"),
            "compose=" + B64(ComposeText()),
            "units=" + string.Join(' ', units)) + "\n";
    }

    private static CommandResult Ok(string output) => new() { ExitCode = 0, Output = output };
    internal static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}

/// <summary>step-ca on "local" plus one simulated proxy per server id.</summary>
internal sealed class SimulatedFleet(TestCa ca) : IHostCommandExecutor
{
    public Dictionary<string, SimulatedProxyHost> Hosts { get; } = new();
    public List<(string Server, string Script)> Commands { get; } = new();
    public int Signed { get; private set; }
    /// <summary>The server record each host command travels with (its own client credential).</summary>
    public Func<string, ServerConfig?>? Records { get; set; }
    /// <summary>Hosts a command could not reach because the record's own credential was refused.</summary>
    public List<string> LockedOut { get; } = new();

    public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
        CancellationToken ct = default, int? maxOutputChars = null)
    {
        var script = MtlsCertificateRenewalTests.Decode(command);
        lock (Commands) Commands.Add((serverId, script));
        if (serverId == "local" && script.Contains("step certificate sign", StringComparison.Ordinal))
            return BeforeSign is { } gate ? SignAfterAsync(gate, script) : Task.FromResult(Sign(script));
        if (!Hosts.TryGetValue(serverId, out var host))
            return Task.FromResult(new CommandResult { ExitCode = 1, Error = "no such host" });
        // Every host command travels over the record's own credential: if the host does not admit it, Whiskers has
        // locked itself out and nothing gets through (review r1 MINOR 7).
        if (!host.Unreachable && Records?.Invoke(serverId) is { } record && !host.Admits(record))
        {
            LockedOut.Add(serverId);
            return Task.FromResult(new CommandResult { ExitCode = 1, Error = "handshake refused: the record's own credential is not admitted" });
        }
        return Task.FromResult(host.Execute(script));
    }

    /// <summary>Awaited before step-ca signs: lets a test hold issues after they chose their name.</summary>
    public Func<Task>? BeforeSign { get; set; }

    private async Task<CommandResult> SignAfterAsync(Func<Task> gate, string script)
    {
        await gate();
        return Sign(script);
    }

    private CommandResult Sign(string script)
    {
        lock (Commands)
        {
            var csrB64 = script.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("echo ", StringComparison.Ordinal)).Split(' ')[1];
            var bundle = ca.Sign(Encoding.ASCII.GetString(Convert.FromBase64String(csrB64)), out var leaf, stepLeafEku: true);
            leaf.Dispose();
            Signed++;
            return new CommandResult { ExitCode = 0, Output = SimulatedProxyHost.B64(bundle) };
        }
    }
}

/// <summary>A data directory with the fleet CA and the shared client credential, a store, a fleet and the service with
/// its seams wired to the simulated hosts.</summary>
internal sealed class CredentialWorld : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "whiskers-r144-" + Guid.NewGuid().ToString("N"));
    public TestCa Ca { get; } = new();
    public SimulatedFleet Fleet { get; }
    public MutableServerConfig Servers { get; }
    public MtlsClientCredentialStore Store { get; }
    public MtlsCertificateService Service { get; }
    public DataPathOptions Paths { get; }
    public string MtlsDir => Paths.MtlsDir;
    /// <summary>Contract 0015: keys are written sealed by a usable vault and loaded through the app's loader.</summary>
    public SealedKeyFiles KeyFiles { get; }
    public MtlsClientKeyLoader KeyLoader { get; }

    /// <summary>The PEM of a key file as the app reads it (a sealed key opened, a plaintext one as it is).</summary>
    public string ReadKeyPem(string path)
    {
        using var material = KeyFiles.Read(path, SealedKeyPurposes.MtlsClientKey(Paths, path));
        return material.Pem.ToString();
    }
    private int _run;

    public CredentialWorld(bool withSharedCredential = true, params (string Id, string Ip)[] hosts)
    {
        if (hosts.Length == 0) hosts = [("rabenhof", "100.64.0.9")];
        Paths = new DataPathOptions(Root);
        KeyFiles = new SealedKeyFiles(TestVault.Usable, Paths);
        KeyLoader = new MtlsClientKeyLoader(KeyFiles, Paths);
        Directory.CreateDirectory(MtlsDir);
        File.WriteAllText(Path.Combine(MtlsDir, "ca.crt"), Ca.BundlePem);
        if (withSharedCredential) WriteShared();

        Fleet = new SimulatedFleet(Ca);
        Servers = new MutableServerConfig();
        foreach (var (id, ip) in hosts)
        {
            Fleet.Hosts[id] = new SimulatedProxyHost(Ca, ip) { Loader = KeyLoader.Load };
            Servers.AddServerAsync(new ServerConfig
            {
                Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], ConnectionType = ConnectionType.TCP, TcpHost = ip, TcpPort = 2376,
                TcpUseTls = true,
                TcpClientCertPath = Path.Combine(MtlsDir, "client.crt"),
                TcpClientKeyPath = Path.Combine(MtlsDir, "client.key"),
                TcpCaCertPath = Path.Combine(MtlsDir, "ca.crt"),
            }).GetAwaiter().GetResult();
        }

        Fleet.Records = Servers.GetServer;
        Store = new MtlsClientCredentialStore(Fleet, NullLogger<MtlsClientCredentialStore>.Instance, Paths, Servers, KeyFiles);
        Service = new MtlsCertificateService(Fleet, Servers, NullLogger<MtlsCertificateService>.Instance, Store)
        {
            RestartGrace = TimeSpan.Zero,
            ConfirmInterval = TimeSpan.Zero,
            ConfirmAttempts = 2,
        };
        Service.ProbeCredential = (server, _) => Task.FromResult(Host(server.Id).Probe(server));
        Service.RunConfirmation = (server, command, _) =>
        {
            // Only a connection the host admits can carry the script (the one-off client of the NEW credential).
            var host = Host(server.Id);
            if (host.Probe(server) != MtlsProbeOutcome.Admitted) return Task.FromResult(("", "handshake refused", 1));
            return Task.FromResult(host.Confirm(MtlsCertificateRenewalTests.Decode(command)));
        };
        Service.VerifyConnection = (id, _) => Task.FromResult(Host(id).Probe(Servers.GetServer(id)!) == MtlsProbeOutcome.Admitted);
        Service.UnitSuffix = () => "r" + Interlocked.Increment(ref _run);
    }

    public SimulatedProxyHost Host(string id) => Fleet.Hosts[id];
    public ServerConfig Server(string id) => Servers.GetServer(id)!;

    public void WriteShared()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + MtlsCertificateService.SharedClientCommonName, key, HashAlgorithmName.SHA256);
        var bundle = Ca.Sign(req.CreateSigningRequestPem(), out var leaf);
        leaf.Dispose();
        File.WriteAllText(Path.Combine(MtlsDir, "client.crt"), bundle);
        File.WriteAllText(Path.Combine(MtlsDir, "client.key"), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>The record of <paramref name="id"/> with another credential (for probes).</summary>
    public ServerConfig With(string id, MtlsClientCredentialPaths paths)
    {
        var copy = Server(id).Clone();
        copy.TcpClientCertPath = paths.Cert;
        copy.TcpClientKeyPath = paths.Key;
        copy.TcpCaCertPath = paths.Ca;
        return copy;
    }

    public MtlsClientCredentialPaths ConfigPaths(string id) =>
        new(Server(id).TcpClientCertPath!, Server(id).TcpClientKeyPath!, Store.CaPath, 0);

    public void Dispose()
    {
        Ca.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
