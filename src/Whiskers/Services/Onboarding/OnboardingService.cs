using Whiskers.Services.Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Automates integrating a freshly-added server into the mesh + mTLS fleet: installs Tailscale
/// (surfacing the interactive login link to the UI), deploys node_exporter, issues a per-host
/// server certificate from step-ca, deploys docker-socket-proxy + ghostunnel (mTLS), wires the host
/// into the VictoriaMetrics scrape config, and switches the server to TCP+mTLS + Prometheus metrics.
///
/// Every command runs through <see cref="IHostCommandExecutor"/>: on the NEW host over the SSH
/// bootstrap connection, and on "local" (the controller host) for step-ca and the scrape config.
/// Progress is reported per step (<see cref="OnboardingProgress"/>); the string overload reports the Tailscale login URL
/// with the <see cref="LinkMarker"/> prefix so the UI can render it as a clickable link.
///
/// W3: the run is tracked per <see cref="OnboardingStep"/> and returns an <see cref="OnboardingResult"/>
/// with the failed step and an actionable hint. Every step is idempotent (existing installs are
/// detected and skipped), so a re-run after any failure resumes safely. Command strings are built by
/// <see cref="OnboardingCommands"/> and <see cref="RescueCommands"/> (unit-tested; user input only ever passes through
/// <see cref="OnboardingCommands.Slug"/> or base64).
///
/// Contract 0018: the run order is CloudBootstrap (only with a bootstrap method), HostKey (the SSH host key is pinned
/// before the first SSH command), Bootstrap … Verify, BootstrapCleanup (a generated key's line is removed from the
/// host, then the key). A run that fails while the bootstrap line is on the host reports a standing bootstrap key.
/// </summary>
public class OnboardingService : IOnboardingService
{
    public const string LinkMarker = "::TS_LOGIN::";

    /// <summary>The environment variable the Tailscale auth key reaches the host's shell in (D9, HostCommandSecrets).</summary>
    internal const string TailscaleAuthKeyVariable = "WHISKERS_TS_AUTHKEY";

    private readonly IHostCommandExecutor _exec;
    private readonly IServerConfigService _serverConfig;
    private readonly IDockerService _docker;
    private readonly ILogger<OnboardingService> _logger;
    private readonly IMtlsCertificateService _certificates;
    private readonly IMtlsClientCredentialStore _clientCredentials;
    private readonly ISshHostKeyPins? _hostKeys;
    private readonly IReadOnlyList<ICloudBootstrap> _bootstraps;
    private readonly IVaultService? _vault;
    private readonly IOptionsMonitor<OnboardingSettings>? _settings;
    private readonly IOnboardingRunRecorder? _runs;

    // Deployment-specific paths (match this Whiskers install). Since R1-44 each host gets its own client
    // credential from IMtlsClientCredentialStore ({MtlsDir}/hosts/{id}/); the CA bundle stays {MtlsDir}/ca.crt.
    // The per-host SERVER cert is issued on the host itself.
    private const string DockerProxyPort = "2376";
    private const string VmComposeDirOnHost = "/opt/telemetry-vm";   // holds scrape.yml + compose
    private const string ScrapeFileOnHost = "/opt/telemetry-vm/scrape.yml";

    /// <summary>Step 8 reads, edits and writes the one scrape config of the Whiskers host: two onboardings at once would
    /// otherwise each write their own edit over the other's. Static: every service instance edits the same file.</summary>
    private static readonly SemaphoreSlim ScrapeConfigGate = new(1, 1);

    /// <summary>The scrape config is decoded strictly: invalid UTF-8 fails the step instead of being written back
    /// with replacement characters.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public OnboardingService(
        IHostCommandExecutor exec,
        IServerConfigService serverConfig,
        IDockerService docker,
        IMtlsCertificateService certificates,
        IMtlsClientCredentialStore clientCredentials,
        ILogger<OnboardingService> logger,
        IMtlsClientKeyLoader? keyLoader = null,
        ISshHostKeyPins? hostKeys = null,
        IEnumerable<ICloudBootstrap>? bootstraps = null,
        IVaultService? vault = null,
        IOptionsMonitor<OnboardingSettings>? settings = null,
        IOnboardingRunRecorder? runs = null)
    {
        _runs = runs;
        _exec = exec;
        _certificates = certificates;
        _clientCredentials = clientCredentials;
        // Contract 0015 [mTLS layer]: the probe opens the sealed client key in memory. Without a loader nothing is proven.
        ProbeCredential = (server, ct) => keyLoader is null
            ? Task.FromResult(MtlsProbeOutcome.NotChecked)
            : MtlsClientProbe.ProbeAsync(server, keyLoader, TimeSpan.FromSeconds(10), ct);
        _serverConfig = serverConfig;
        _docker = docker;
        _logger = logger;
        _hostKeys = hostKeys;
        _bootstraps = bootstraps?.ToList() ?? [];
        _vault = vault;
        _settings = settings;
    }

    public Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
        IProgress<string> progress,
        CancellationToken ct = default) =>
        OnboardServerAsync(serverId, mayReplaceMtlsState, decidedConnection, new OnboardingOptions(BootstrapMethod.None),
            new LineProgress(progress), ct);

    /// <summary>Adapts per-step progress to the line-based UI: the login link becomes a <see cref="LinkMarker"/> line.</summary>
    private sealed class LineProgress(IProgress<string> lines) : IProgress<OnboardingProgress>
    {
        public void Report(OnboardingProgress value) =>
            lines.Report(value.TailscaleLoginUrl is { } url ? $"{LinkMarker}{url}" : value.Message);
    }

    public async Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
        OnboardingOptions options, IProgress<OnboardingProgress> progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        var standing = new StandingKeyProbe();
        // Contract 0018 part E: every line also goes to the run's record (get_onboarding_status), once the run holds
        // the host lock — a call refused because another run holds it does not replace that run's snapshot.
        progress = new RecordingProgress(progress, standing);
        var result = await RunAsync(serverId, mayReplaceMtlsState, decidedConnection, options, progress, standing, ct);
        // Contract 0018 (INV-64): a run that ends Failed while the bootstrap line is on the host says so on every surface.
        // The record was read while the run still held the host lock (INV-59: no lookup outside it).
        if (!result.Success && standing.Fingerprint is { } fingerprint)
        {
            var warning = BootstrapCleanup.StandingKeyWarning(fingerprint, standing.Name ?? serverId);
            progress.Report(new OnboardingProgress(result.FailedStep ?? OnboardingStep.Bootstrap, $"⚠️ {warning}"));
            MutationScope.Note($"standingBootstrapKey: true, fingerprint {fingerprint}", success: false);
            result = result with
            {
                StandingBootstrapKey = true,
                CleanupWarnings = [.. result.CleanupWarnings.Where(w => w != warning), warning],
            };
        }
        standing.Record?.Finish(result);
        return result;
    }

    /// <summary>The standing bootstrap line as the record named it when the run released the host lock, and the run's
    /// record in <see cref="IOnboardingRunRecorder"/> (contract 0018 part E) once the run holds the host lock.</summary>
    private sealed class StandingKeyProbe
    {
        public string? Fingerprint { get; set; }
        public string? Name { get; set; }
        public IOnboardingRunRecord? Record { get; set; }
    }

    /// <summary>Reports to the caller's progress and to the run's record (when there is one yet).</summary>
    private sealed class RecordingProgress(IProgress<OnboardingProgress> caller, StandingKeyProbe run) : IProgress<OnboardingProgress>
    {
        public void Report(OnboardingProgress value)
        {
            run.Record?.Report(value);
            caller.Report(value);
        }
    }

    private async Task<OnboardingResult> RunAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
        OnboardingOptions options, IProgress<OnboardingProgress> progress, StandingKeyProbe standing, CancellationToken ct)
    {
        var completed = new List<OnboardingStep>();
        var step = options.AbandonBootstrap ? OnboardingStep.BootstrapCleanup
            : options.Bootstrap != BootstrapMethod.None ? OnboardingStep.CloudBootstrap : OnboardingStep.Bootstrap;
        var cleanupWarnings = new List<string>();
        SshHostKeyPin? hostKey = null;
        void Say(string message) => progress.Report(new OnboardingProgress(step, message));
        OnboardingResult Failed(string detail) => Fail(progress, completed, step, detail) with
        {
            HostKey = hostKey, CleanupWarnings = cleanupWarnings.ToList(),
        };

        Models.ServerConfig server;
        string slug;
        SemaphoreSlim? hostLock = null;
        try
        {
            // R1-44 (contract 0007, amendment 10; review r3 MAJOR 1) and INV-59 (#187, contract 0012 A12): the whole run
            // holds the store's per-host lock, the one every certificate step and server removal take — taken before
            // the server is looked up. A running migration, renewal, rotation or removal makes onboarding refuse
            // instead of interleaving slots, and while the run holds it the server cannot be removed, so its id cannot
            // come to select another server.
            var gate = _clientCredentials.HostLock(serverId);
            if (!await gate.WaitAsync(0, ct))
                return Failed($"Für {serverId} läuft gerade ein Zertifikatsschritt (Migration, Erneuerung, Rotation oder Entfernen); " +
                              "Onboarding erneut starten, sobald er fertig ist.");
            hostLock = gate;
            standing.Record = _runs?.Begin(serverId, MutationScope.Current?.OperationId ?? Guid.NewGuid().ToString("N"));
            // INV-59 (#187): exactly the server the decision named, by its id — never another server that a display
            // name or an id in another case would select.
            if (Exact(serverId)?.Clone() is not { } found)   // edited below, saved once at the switchover
            {
                Say("❌ Server nicht gefunden.");
                return OnboardingResult.Fail(completed, step, "Server nicht gefunden.");
            }
            server = found;
            slug = OnboardingCommands.Slug(server.Name);
            if (ChangedHost(server, decidedConnection) is { } moved)
                return Failed(moved);
            // INV-59 (#187): admitted as a first onboarding, but the host holds mTLS state now (another onboarding
            // finished since the decision): replacing it needs the control-plane approval, so nothing runs.
            if (!mayReplaceMtlsState && MtlsOnboardingState.Holds(server, _clientCredentials))
                return Failed($"{server.Name} hat inzwischen mTLS-Zustand (ein anderes Onboarding ist fertig geworden). " +
                              "Erneutes Onboarding braucht die Freigabe eines Admins; bitte neu starten.");

            // Contract 0018 (INV-63, INV-64): a rescue run injected its key and stopped before the pin from the disk. A run
            // without the bootstrap would now pin the host on first contact with the stored bootstrap key, and the VM may
            // still be in rescue: refused on every surface (the UI's RescueResumeForced, MCP onboard_server) before any
            // command. Only the resume (it pins the keys read from the disk) or the abandon may go on.
            if (!options.AbandonBootstrap && options.Bootstrap == BootstrapMethod.None
                && RescueResumeRequired(server) is { } resumeOnly)
            {
                step = OnboardingStep.HostKey;
                Say($"❌ {resumeOnly}");
                return Failed(resumeOnly);
            }

            // Abandon (contract 0018): only BootstrapCleanup, then the run ends.
            if (options.AbandonBootstrap)
            {
                step = OnboardingStep.BootstrapCleanup;
                Say("🧹 Bootstrap-Zugang entfernen…");
                if (await AbandonAsync(server, cleanupWarnings, ct) is { } abandonError) return Failed(abandonError);
                completed.Add(step);
                return OnboardingResult.Ok(completed) with { CleanupWarnings = cleanupWarnings.ToList() };
            }

            // 0) Cloud bootstrap (contract 0018 part D) ------------------------------------------------
            if (options.Bootstrap != BootstrapMethod.None)
            {
                step = OnboardingStep.CloudBootstrap;
                if (server.SshHostKeySource == SshHostKeySource.RescueDisk && server.SshKeyFileName is not null)
                {
                    Say("☁️ Cloud-Bootstrap bereits erledigt (Host-Key von der Platte gepinnt) — übersprungen (Resume).");
                }
                else
                {
                    var bootstrap = _bootstraps.FirstOrDefault(b => b.Method == options.Bootstrap);
                    if (bootstrap is null) return Failed($"Kein Bootstrap-Verfahren '{options.Bootstrap}' verfügbar.");
                    Say("☁️ Zugang über das Hetzner-Rescue-System einrichten (zwei Neustarts)…");
                    var prepared = await bootstrap.PrepareAsync(server, options, progress, ct);
                    cleanupWarnings.AddRange(prepared.CleanupWarnings);
                    if (!prepared.Success) return Failed(prepared.Error ?? "Cloud-Bootstrap fehlgeschlagen.");
                    hostKey = prepared.HostKeyFromDisk;
                }
                completed.Add(step);
                if (Exact(serverId)?.Clone() is not { } afterBootstrap) return Failed($"{server.Name} wurde inzwischen entfernt.");
                server = afterBootstrap;
            }

            // 1) Host key (contract 0018 part C): pinned before the first SSH command --------------------
            step = OnboardingStep.HostKey;
            if (server.ConnectionType == ConnectionType.SSH)
            {
                if (_hostKeys is null) return Failed("Kein Host-Key-Dienst konfiguriert; ohne Pin wird kein SSH-Befehl gesendet.");
                Say("🔐 SSH-Host-Key prüfen…");
                try
                {
                    if (_hostKeys.PinOf(server) is { PinnedAtUtc: not null } recorded && server.SshHostKeyFingerprint is not null)
                    {
                        await _hostKeys.VerifyAsync(server, ct);
                        hostKey = recorded;
                    }
                    else
                    {
                        hostKey = await _hostKeys.PinFirstContactAsync(server, ct);
                    }
                }
                catch (SshHostKeyMismatchException mismatch)
                {
                    Say($"❌ {mismatch.Message}");
                    return Failed(mismatch.Message) with { HostKeyMismatch = (mismatch.PinnedFingerprint, mismatch.ServedFingerprint) };
                }
                Say($"🔐 Host-Key {hostKey.KeyType} {hostKey.FingerprintSha256} ({hostKey.Source})");
                completed.Add(step);
                if (Exact(serverId)?.Clone() is { } pinned) server = pinned;
            }

            // 2) Bootstrap reachability ------------------------------------------------------------
            step = OnboardingStep.Bootstrap;
            Say("① Bootstrap-Verbindung prüfen…");
            // Contract 0018 A5: which destination and credential this run uses, from the record read above (a retry reads
            // it again). The credential is named, never its value.
            Say(BootstrapLine(server));
            var reach = await _exec.ExecuteAsync(serverId, "echo ok", TimeSpan.FromSeconds(20), ct);
            if (!reach.Success)
                return Failed($"{BootstrapReason(reach)} (exit {reach.ExitCode}): {Trunc(reach.Error)}");
            completed.Add(step);

            // 3) Install + start Tailscale ---------------------------------------------------------
            step = OnboardingStep.TailscaleInstall;
            Say("② Tailscale installieren…");
            await Sh(serverId, "command -v tailscale >/dev/null || curl -fsSL https://tailscale.com/install.sh | sudo sh", TimeSpan.FromMinutes(3), ct);
            await ShTry(serverId, "sudo systemctl enable --now tailscaled", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 4) Bring up Tailscale. If the node is already authenticated (e.g. a re-run after a
            // failed onboarding), skip the login dance entirely and reuse the existing tailnet IP —
            // this is what makes a re-run a RESUME.
            step = OnboardingStep.TailscaleLogin;
            string? tsIp = null;
            var already = await Sh(serverId, "tailscale status --json 2>/dev/null | grep -m1 '\"BackendState\"'", TimeSpan.FromSeconds(20), ct);
            if (already.Output.Contains("Running"))
            {
                var ip0 = await Sh(serverId, "tailscale ip -4 2>/dev/null | head -1", TimeSpan.FromSeconds(20), ct);
                tsIp = string.IsNullOrWhiteSpace(ip0.Output) ? null : ip0.Output.Trim();
                if (tsIp != null) Say("③ Node ist bereits im Tailnet — Login übersprungen (Resume).");
            }

            if (tsIp == null)
            {
                // D9: with an onboarding auth key in the vault the node enrolls without a person; otherwise the link.
                var authKey = OnboardingAuthKey();
                if (authKey is not null)
                {
                    Say("③ Tailscale mit dem Onboarding-Auth-Key anmelden…");
                    using (HostCommandSecrets.Provide([new(TailscaleAuthKeyVariable, authKey)]))
                        await Sh(serverId, OnboardingCommands.TailscaleUpWithAuthKey(slug, TailscaleAuthKeyVariable), TimeSpan.FromSeconds(60), ct);
                    MutationScope.Note("tailscale: enrolled with the onboarding auth key");
                    Say("✅ Mit Auth-Key angemeldet (enrolled with auth key).");
                }
                else
                {
                    Say("③ Tailscale starten…");
                    await ShTry(serverId, "sudo systemctl reset-failed ts-up", TimeSpan.FromSeconds(10), ct);
                    await Sh(serverId, OnboardingCommands.TailscaleUp(slug), TimeSpan.FromSeconds(20), ct);

                    Say("④ Auf Tailscale-Login warten…");
                    var loginUrl = await PollAsync(async () =>
                    {
                        var r = await Sh(serverId, "sudo journalctl -u tailscaled --since '90 seconds ago' --no-pager 2>/dev/null | grep -oE 'https://login\\.tailscale\\.com/[A-Za-z0-9/]+' | tail -1", TimeSpan.FromSeconds(20), ct);
                        var url = r.Output.Trim();
                        return string.IsNullOrEmpty(url) ? null : url;
                    }, attempts: 15, delay: TimeSpan.FromSeconds(3), ct);

                    if (loginUrl == null)
                    {
                        Say("❌ Kein Tailscale-Login-Link gefunden (Timeout).");
                        return Failed("Kein Tailscale-Login-Link gefunden (Timeout).");
                    }
                    // The link itself is not audited: the event only says that one was issued.
                    MutationScope.Note("tailscale: login link issued");
                    progress.Report(new OnboardingProgress(step, "Tailscale-Login-Link", loginUrl));
                    Say("⏳ Bitte den Link öffnen und den Node im Browser bestätigen…");
                }

                // Wait until the node is authenticated + has an IP
                tsIp = await PollAsync(async () =>
                {
                    var st = await Sh(serverId, "tailscale status --json 2>/dev/null | grep -m1 '\"BackendState\"'", TimeSpan.FromSeconds(20), ct);
                    if (!st.Output.Contains("Running")) return null;
                    var ip = await Sh(serverId, "tailscale ip -4 2>/dev/null | head -1", TimeSpan.FromSeconds(20), ct);
                    var v = ip.Output.Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }, attempts: 100, delay: TimeSpan.FromSeconds(3), ct); // ~5 min for the user to click

                if (tsIp == null)
                {
                    Say("❌ Node nicht verbunden (Timeout beim Login).");
                    return Failed("Node nicht verbunden (Timeout beim Login).");
                }
            }

            // The tailnet IP flows into shell commands and compose files below — accept only a real
            // IP literal, never arbitrary command output.
            // IPAddress.TryParse alone would admit an IPv6 zone id carrying arbitrary text (fe80::1%';…): only an IPv4
            // literal passes, and from here on only its normalised form is used.
            if (OnboardingCommands.TailnetIPv4(tsIp) is not { } normalisedIp)
            {
                Say("❌ Unerwartete Tailscale-IP (keine IPv4-Adresse).");
                return Failed("Unerwartete Tailscale-IP (keine IPv4-Adresse).");
            }
            tsIp = normalisedIp;
            completed.Add(step);
            Say($"✅ Mit dem Tailnet verbunden: {tsIp}");

            // 5) Ensure Docker is present — a fresh VPS may not have it; the telemetry + proxy steps
            // below all run `docker compose`. Install via the official convenience script if missing.
            step = OnboardingStep.Docker;
            Say("⑤ Docker sicherstellen…");
            await Sh(serverId, "command -v docker >/dev/null 2>&1 || (curl -fsSL https://get.docker.com | sudo sh)", TimeSpan.FromMinutes(5), ct);
            await ShTry(serverId, "sudo systemctl enable --now docker", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 6) Deploy node_exporter (mesh-only) on the new host ---------------------------------
            step = OnboardingStep.NodeExporter;
            Say("⑥ node-exporter deployen…");
            await WriteFile(serverId, "/opt/telemetry/docker-compose.yml", OnboardingCommands.NodeExporterCompose(tsIp), ct);
            await Sh(serverId, "cd /opt/telemetry && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);
            completed.Add(step);

            // 7) Issue the per-host server cert: the key is generated ON the host and never leaves it; only
            // the CSR travels to step-ca (on local) and the signed certificate travels back. It used to be the
            // other way round — key made on Badwolf, shipped base64 through the command channel.
            step = OnboardingStep.Certificate;
            // Steps 0–4 can take minutes (Tailscale login): continue with the record as it is now, so a change finished
            // meanwhile (an edit) is not overwritten at the switchover (review r4 MINOR 2).
            if (Exact(serverId)?.Clone() is not { } current)
                return Failed($"{server.Name} wurde inzwischen entfernt.");
            server = current;
            if (ChangedHost(server, decidedConnection) is { } movedMeanwhile)
                return Failed(movedMeanwhile);
            if (!mayReplaceMtlsState && MtlsOnboardingState.Holds(server, _clientCredentials))
                return Failed($"{server.Name} hat inzwischen mTLS-Zustand (ein anderes Onboarding ist fertig geworden). " +
                              "Erneutes Onboarding braucht die Freigabe eines Admins; bitte neu starten.");
            Say("⑥ Server-Zertifikat ausstellen (Schlüssel auf dem Host, step-ca signiert)…");
            await _certificates.IssueForNewHostAsync(serverId, MtlsIdentity.ForNewHost(slug, tsIp), ct);
            // R1-44 (D6): the host gets its own client identity right away (generation 1); its proxy never admits the
            // fleet's shared one. The key is made in Whiskers' process and stays in its data directory. A re-run after the
            // switchover reuses the credential the server record names (the record is authoritative); otherwise the
            // credential goes into a new slot.
            var clientIdentity = _clientCredentials.IdentityOf(serverId);
            MtlsClientCredentialPaths clientPaths;
            if (clientIdentity is not null && _clientCredentials.IsManaged(server)
                && File.Exists(server.TcpClientCertPath) && File.Exists(server.TcpClientKeyPath))
            {
                clientPaths = new MtlsClientCredentialPaths(server.TcpClientCertPath!, server.TcpClientKeyPath!,
                    _clientCredentials.CaPath, clientIdentity.Generation);
            }
            else
            {
                clientPaths = await _clientCredentials.IssueAsync(server, clientIdentity?.Generation ?? 1, ct);
                clientIdentity = _clientCredentials.IdentityOf(serverId)
                                 ?? throw new InvalidOperationException("The issued client identity could not be read back.");
            }
            completed.Add(step);

            // 8) Deploy socket-proxy + ghostunnel (mTLS) on the new host -------------------------
            step = OnboardingStep.MtlsProxy;
            Say("⑦ socket-proxy + ghostunnel (mTLS) deployen…");
            await WriteFile(serverId, "/opt/dockerproxy/docker-compose.yml",
                OnboardingCommands.DockerProxyCompose(tsIp, clientIdentity.CommonName), ct);
            await Sh(serverId, "cd /opt/dockerproxy && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);
            completed.Add(step);

            // 9) Wire into the VictoriaMetrics scrape config (on local) + reload -----------------
            step = OnboardingStep.ScrapeConfig;
            Say("⑧ In Scrape-Config eintragen…");
            // Contract 0017 I1: read the file, edit it in C#, write it back base64 as root (sh when root, else sudo sh).
            // The server id and the address become YAML scalars; neither reaches any program text on the host. The
            // read-modify-write is serialised, so concurrent onboardings cannot drop each other's target.
            await ScrapeConfigGate.WaitAsync(ct);
            try
            {
                var scrapeB64 = (await Sh("local", OnboardingCommands.ReadFileB64(ScrapeFileOnHost), TimeSpan.FromSeconds(20), ct)).Output.Trim();
                var scrapeEdit = ScrapeConfigEditor.AddTarget(
                    StrictUtf8.GetString(Convert.FromBase64String(scrapeB64)), serverId, tsIp);
                if (scrapeEdit.Outcome == ScrapeConfigEditOutcome.Added)
                    await Sh("local", OnboardingCommands.WriteFileAsRootB64(ScrapeFileOnHost,
                        Convert.ToBase64String(StrictUtf8.GetBytes(scrapeEdit.Yaml!))), TimeSpan.FromSeconds(30), ct);
                else if (scrapeEdit.Outcome == ScrapeConfigEditOutcome.NoAnchor)
                    Say($"⚠️ {ScrapeFileOnHost} hat keine Zeile 'static_configs:' — Ziel nicht eingetragen.");
            }
            finally
            {
                ScrapeConfigGate.Release();
            }
            await ShTry("local", $"cd {VmComposeDirOnHost} && docker compose restart victoriametrics", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 10) Switch the server to TCP + mTLS + Prometheus ------------------------------------
            step = OnboardingStep.Switchover;
            Say("⑨ Server auf TCP+mTLS umstellen…");
            var vmEndpoint = await ResolveVmEndpoint(ct);
            server.ConnectionType = ConnectionType.TCP;
            server.TcpHost = tsIp;
            server.TcpPort = int.Parse(DockerProxyPort);
            server.TcpUseTls = true;
            server.TcpClientCertPath = clientPaths.Cert;
            server.TcpClientKeyPath = clientPaths.Key;
            server.TcpCaCertPath = clientPaths.Ca;   // the fleet CA bundle {MtlsDir}/ca.crt — no new trust anchor
            server.MetricsSource = MetricsSourceKind.Prometheus;
            server.MetricsEndpoint = vmEndpoint;
            await _serverConfig.UpdateServerAsync(server);
            completed.Add(step);

            // 11) Verify over mTLS -----------------------------------------------------------------
            step = OnboardingStep.Verify;
            Say("⑩ Verifizieren (mTLS)…");
            await Task.Delay(VerifyDelay, ct);
            var info = await _docker.GetServerSystemInfoAsync(serverId);
            if (!info.IsReachable)
            {
                Say($"⚠️ Onboarding fertig, aber mTLS-Verbindung noch nicht erreichbar: {info.Error}");
                return Failed(info.Error ?? "mTLS-Verbindung nicht erreichbar.");
            }
            // The issued slot was pending; the record names it and the connection through it works: now it is active.
            if (_clientCredentials.PendingFor(serverId) is { } pendingSlot
                && MtlsClientCredentialStore.SamePath(server.TcpClientCertPath, pendingSlot.Cert))
                await _clientCredentials.ActivatePendingAsync(serverId, CancellationToken.None);
            await RecordClientIdentityAsync(server, clientIdentity, progress, ct);
            completed.Add(step);

            // 12) Bootstrap clean-up -----------------------------------------------------------------
            // Onboarding succeeded over mTLS → drop the one-time bootstrap credentials so nothing standing remains.
            // Contract 0018 A6: the password is cleared by a store write of a clone of the record this run holds — not an
            // edit of an instance the store may share, and never a re-read of the cached record.
            step = OnboardingStep.BootstrapCleanup;
            var cleared = server.Clone();
            cleared.SshPassword = null;
            await _serverConfig.UpdateServerAsync(cleared);
            server = cleared;
            if (await CleanupAsync(serverId, ct) is { } cleanupError) return Failed(cleanupError);
            completed.Add(step);

            Say($"🎉 Fertig! {server.Name} ist im Mesh, läuft über mTLS ({info.ContainersRunning}/{info.ContainersTotal} Container, {info.OperatingSystem}). SSH-Bootstrap-Key + Passwort wurden entfernt — SSH wird nicht mehr benötigt.");
            return OnboardingResult.Ok(completed) with { HostKey = hostKey, CleanupWarnings = cleanupWarnings.ToList() };
        }
        catch (OperationCanceledException)
        {
            Say("⏹️ Abgebrochen. Erneutes Starten setzt am unterbrochenen Schritt wieder auf (alle Schritte sind idempotent).");
            return OnboardingResult.Fail(completed, step, "Abgebrochen.") with { HostKey = hostKey, CleanupWarnings = cleanupWarnings.ToList() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding failed for {ServerId} at step {Step}", serverId, step);
            return Failed(ex.Message);
        }
        finally
        {
            if (hostLock is not null)
            {
                if (Exact(serverId) is { BootstrapKeyInjectedFingerprint: { } fingerprint } record)
                {
                    standing.Fingerprint = fingerprint;
                    standing.Name = record.Name;
                }
                hostLock.Release();
            }
        }
    }

    /// <summary>
    /// Contract 0018: why a run without a bootstrap may not start on <paramref name="server"/> — a bootstrap key was
    /// injected (<see cref="Models.ServerConfig.BootstrapKeyInjectedFingerprint"/>) and no host key is pinned on the record —
    /// or null. Such a record only resumes the rescue bootstrap (<see cref="BootstrapMethod.HetznerRescueKey"/>) or is
    /// abandoned. Names the fingerprint (public), never a secret.
    /// </summary>
    internal static string? RescueResumeRequired(Models.ServerConfig server) =>
        server.BootstrapKeyInjectedFingerprint is { } fingerprint && server.SshHostKeyFingerprint is null
            ? $"Bootstrap-Key {fingerprint} eingetragen, aber kein Host-Key gepinnt (die VM kann noch im Rescue-System " +
              "sein): ein Lauf ohne Bootstrap wird abgelehnt. Fortsetzen mit dem Rescue-Bootstrap (bootstrap=hetzner_rescue, " +
              "pinnt die Host-Keys von der Platte) oder den Bootstrap-Zugang entfernen (abandon)."
            : null;

    /// <summary>Test seam: the pause before the mTLS verification.</summary>
    internal TimeSpan VerifyDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Step BootstrapCleanup (contract 0018, D12): for a key Whiskers generated, every bootstrap line of the server is
    /// removed from <c>/root/.ssh/authorized_keys</c> (through the host shell the record now uses — mTLS after the
    /// switchover) and read back; only then is the injected fingerprint cleared, the sealed key deleted and the flag
    /// cleared. When the line cannot be removed the key is kept, so a resume or abandon can retry. For a key the user
    /// supplied: only the key is deleted (as before). Returns the reason the step failed, or null.
    /// </summary>
    private async Task<string?> CleanupAsync(string serverId, CancellationToken ct)
    {
        var record = Exact(serverId) ?? throw new InvalidOperationException("The record was removed meanwhile.");
        if (!record.BootstrapKeyGenerated && record.BootstrapKeyInjectedFingerprint is null)
        {
            await _serverConfig.DeleteSshKeyAsync(serverId);
            return null;
        }
        if (record.BootstrapKeyInjectedFingerprint is not null)
        {
            var removal = BootstrapCleanup.Judge(await _exec.ExecuteAsync(serverId, RescueCommands.RemoveMarkerLines(serverId),
                BootstrapCleanup.Bound, ct));
            if (!removal.Removed) return $"Die Bootstrap-Zeile konnte nicht entfernt werden: {removal.Error}";
            MutationScope.Note($"bootstrap line {record.BootstrapKeyInjectedFingerprint} removed from /root/.ssh/authorized_keys");
            var lineGone = (Exact(serverId) ?? record).Clone();
            lineGone.BootstrapKeyInjectedFingerprint = null;
            await _serverConfig.UpdateServerAsync(lineGone);
        }
        await _serverConfig.DeleteSshKeyAsync(serverId);
        if (Exact(serverId)?.Clone() is { } keyGone)
        {
            keyGone.BootstrapKeyGenerated = false;
            await _serverConfig.UpdateServerAsync(keyGone);
        }
        MutationScope.Note("generated bootstrap key deleted");
        return null;
    }

    /// <summary>Abandon (contract 0018): only BootstrapCleanup. Without the sealed key and without an mTLS shell no host
    /// command is possible: the fingerprint is cleared and the dead line recorded as a warning.</summary>
    private async Task<string?> AbandonAsync(Models.ServerConfig server, List<string> warnings, CancellationToken ct)
    {
        if (server.BootstrapKeyInjectedFingerprint is not { } fingerprint)
        {
            if (server.BootstrapKeyGenerated) return await CleanupAsync(server.Id, ct);
            MutationScope.Note("abandon: no bootstrap line recorded; nothing to remove");
            return null;
        }
        var canRun = server.ConnectionType == ConnectionType.TCP
                     || (server.ConnectionType == ConnectionType.SSH && server.SshKeyFileName is not null
                         && _serverConfig.GetSshKeyPath(server) is not null);
        MutationScope.Note($"abandon run for bootstrap line {fingerprint}");
        if (canRun && server.ConnectionType == ConnectionType.SSH)
        {
            // INV-63: the removal talks SSH only to a host whose key is verified (a recorded pin or a known_hosts line);
            // without the pins service no SSH command is sent at all. A line was injected but nothing pinned: the run
            // stopped between injection and the pin from the disk, and the VM may still be in rescue — a first contact now
            // could trust the rescue system's key into known_hosts (contract 0018). Refused; the resume pins from the disk.
            if (_hostKeys is null) return "Kein Host-Key-Dienst konfiguriert; ohne Pin wird kein SSH-Befehl gesendet.";
            try
            {
                if (await _hostKeys.BackfillAsync(server, ct) is null)
                    return "Kein gepinnter Host-Key: der Lauf endete nach dem Eintragen des Bootstrap-Keys und vor dem Pin " +
                           "(die VM kann noch im Rescue-System sein). Bitte das Onboarding fortsetzen (Resume) — es pinnt die " +
                           "Host-Keys von der Platte und entfernt die Zeile danach.";
                await _hostKeys.VerifyAsync(server, ct);
            }
            catch (SshHostKeyMismatchException mismatch)
            {
                return mismatch.Message;
            }
        }
        if (canRun) return await CleanupAsync(server.Id, ct);
        var dead = BootstrapCleanup.DeadLineWarning(fingerprint, server.SshHost);
        warnings.Add(dead);
        MutationScope.Note(dead);
        var cleared = server.Clone();
        cleared.BootstrapKeyInjectedFingerprint = null;
        cleared.BootstrapKeyGenerated = false;
        await _serverConfig.UpdateServerAsync(cleared);
        return null;
    }

    /// <summary>D9: the onboarding Tailscale auth key from the vault, or null when none is configured. A configured key
    /// that cannot be read (vault locked, entry gone) fails the step — never a silent fall-back.</summary>
    private string? OnboardingAuthKey()
    {
        var reference = _settings?.CurrentValue.Tailscale.AuthKeyRef;
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (_vault is null || !_vault.IsEnabled)
            throw new InvalidOperationException("Ein Onboarding-Auth-Key ist eingestellt, aber der Vault ist nicht nutzbar.");
        return _vault.GetSecret(reference)
               ?? throw new InvalidOperationException("Der eingestellte Onboarding-Auth-Key fehlt im Vault.");
    }

    /// <summary>Contract 0018 R4: why the bootstrap command failed — host, user, key, password or host key.</summary>
    internal static string BootstrapReason(CommandResult r)
    {
        var e = (r.Error ?? "") + "\n" + (r.Output ?? "");
        if (e.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || e.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return "Host-Key: der Host zeigt einen anderen SSH-Host-Key als den gepinnten";
        if (e.Contains("SSH user", StringComparison.Ordinal)) return "Benutzer: der SSH-Benutzer fehlt oder ist ungültig";
        if (e.Contains("SSH key not found", StringComparison.Ordinal) || e.Contains("sealed", StringComparison.OrdinalIgnoreCase))
            return "Schlüssel: der hinterlegte SSH-Schlüssel ist nicht verfügbar";
        if (e.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return e.Contains("password", StringComparison.OrdinalIgnoreCase) && !e.Contains("publickey", StringComparison.OrdinalIgnoreCase)
                ? "Passwort: der Host lehnt das Passwort ab"
                : "Schlüssel/Passwort: der Host lehnt die Anmeldung ab (Schlüssel nicht in authorized_keys oder falsches Passwort)";
        if (e.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) || e.Contains("No route", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || e.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || e.Contains("SSH host", StringComparison.Ordinal))
            return "Host: der Host ist unter dieser Adresse und diesem Port nicht erreichbar";
        return "Bootstrap-Befehl fehlgeschlagen";
    }

    /// <summary>Seam for tests: a probe of one client credential against the new host (one-off connection).</summary>
    internal Func<Models.ServerConfig, CancellationToken, Task<MtlsProbeOutcome>> ProbeCredential { get; set; }

    /// <summary>
    /// R1-44 (D6, amendments 2 and 3): the phase-5-equivalent record of a new host. Its proxy was written with only the
    /// host's own name; the record says so only once the shared credential was refused there with a TLS alert — or when
    /// there is no shared credential to try (fresh install, retired). Otherwise it stays at phase 4 and the page says why.
    /// </summary>
    private async Task RecordClientIdentityAsync(Models.ServerConfig server, MtlsClientIdentity identity, IProgress<OnboardingProgress> progress, CancellationToken ct)
    {
        var shared = _clientCredentials.SharedPaths;
        var sharedExists = File.Exists(shared.Cert) && File.Exists(shared.Key);
        var refused = false;
        if (sharedExists)
        {
            var probe = server.Clone();
            probe.TcpClientCertPath = shared.Cert;
            probe.TcpClientKeyPath = shared.Key;
            probe.TcpCaCertPath = _clientCredentials.CaPath;
            refused = await ProbeCredential(probe, ct) == MtlsProbeOutcome.Refused;
            if (!refused)
                progress.Report(new OnboardingProgress(OnboardingStep.Verify,
                    "⚠️ Ob der Host die gemeinsame Client-Identität ablehnt, ließ sich nicht prüfen. Die Migration " +
                    "(„Migrieren“) wiederholt die Prüfung."));
        }
        await _clientCredentials.WriteJournalAsync(server.Id, new MtlsRolloutJournal(MtlsRolloutJournal.Onboard,
            identity.CommonName, identity.Generation, sharedExists && !refused ? 4 : 5,
            sharedExists ? MtlsCertificateService.CommonNameOf(shared.Cert) : null, refused, DateTime.UtcNow,
            SharedCredentialAbsent: !sharedExists), ct);
    }

    /// <summary>
    /// Contract 0018 A5 (R1, R4): the SSH destination and the credential the bootstrap uses, in the executor's order (a
    /// transient password wins over the key, <see cref="HostCommandExecutor"/>). Names the key file or "Passwort" — never
    /// a value, never a prefix of one.
    /// </summary>
    internal string BootstrapLine(Models.ServerConfig server)
    {
        if (server.ConnectionType != ConnectionType.SSH)
            return $"🔑 Bootstrap über die bestehende {server.ConnectionType}-Verbindung (kein SSH-Zugang nötig).";
        var destination = $"{server.SshUser}@{server.SshHost}:{server.SshPort}";
        string credential;
        if (!string.IsNullOrEmpty(server.SshPassword))
            credential = "Passwort";
        else if (string.IsNullOrEmpty(server.SshKeyFileName))
            credential = "kein Schlüssel hinterlegt und kein Passwort";
        else if (_serverConfig.GetSshKeyPath(server) is null)
            credential = $"Schlüsseldatei {server.SshKeyFileName} (fehlt)";
        else
            credential = $"Schlüsseldatei {server.SshKeyFileName}";
        return $"🔑 Bootstrap: ssh {destination} mit {credential}";
    }

    /// <summary>Reports the per-step hint + technical detail and builds the failure result.</summary>
    private static OnboardingResult Fail(IProgress<OnboardingProgress> progress, List<OnboardingStep> completed, OnboardingStep step, string detail)
    {
        progress.Report(new OnboardingProgress(step, $"❌ {OnboardingResult.Hint(step)}"));
        progress.Report(new OnboardingProgress(step,
            "↻ Erneutes Starten des Onboardings ist gefahrlos — abgeschlossene Schritte werden erkannt und übersprungen."));
        return OnboardingResult.Fail(completed, step, $"{OnboardingResult.Hint(step)} (Details: {Trunc(detail)})");
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The server whose id is exactly <paramref name="serverId"/> (INV-59, #187): <c>GetServer</c> also matches an
    /// id in another case and a display name, which could select another server once this one is gone.</summary>
    private Models.ServerConfig? Exact(string serverId) =>
        _serverConfig.GetServer(serverId) is { } s && string.Equals(s.Id, serverId, StringComparison.Ordinal) ? s : null;

    /// <summary>INV-59 (#187): why the run may not go on when the record no longer reaches the host it was decided on
    /// (or that host is unknown); null when it still does.</summary>
    private static string? ChangedHost(Models.ServerConfig server, string? decidedConnection) =>
        decidedConnection is not null
        && string.Equals(MtlsOnboardingState.ConnectionOf(server), decidedConnection, StringComparison.Ordinal)
            ? null
            : $"Die Verbindung von {server.Name} wurde seit der Entscheidung geändert; das Onboarding gilt nur für den " +
              "Host, für den es entschieden (und ggf. freigegeben) wurde. Bitte neu starten.";

    private async Task<CommandResult> Sh(string serverId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var r = await _exec.ExecuteAsync(serverId, command, timeout, ct);
        if (!r.Success)
            throw new InvalidOperationException($"Befehl fehlgeschlagen (exit {r.ExitCode}): {Trunc(command)}\n{Trunc(r.Error)}{Trunc(r.Output)}");
        return r;
    }

    private async Task<CommandResult> ShTry(string serverId, string command, TimeSpan timeout, CancellationToken ct)
        => await _exec.ExecuteAsync(serverId, command, timeout, ct);

    // Write a text file on the target by base64-decoding it server-side (no shell-quoting issues).
    private async Task WriteFile(string serverId, string path, string content, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        await WriteFileB64(serverId, path, b64, ct);
    }

    private async Task WriteFileB64(string serverId, string path, string b64, CancellationToken ct)
        => await Sh(serverId, OnboardingCommands.WriteFileB64(path, b64), TimeSpan.FromSeconds(30), ct);

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> probe, int attempts, TimeSpan delay, CancellationToken ct) where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { var v = await probe(); if (v != null) return v; } catch { /* keep polling */ }
            await Task.Delay(delay, ct);
        }
        return null;
    }

    private async Task<string> ResolveVmEndpoint(CancellationToken ct)
    {
        // VictoriaMetrics is bound to the controller host's tailscale IP on :8428.
        try
        {
            var r = await _exec.ExecuteAsync("local", "tailscale ip -4 | head -1", TimeSpan.FromSeconds(15), ct);
            var ip = r.Output.Trim();
            if (!string.IsNullOrEmpty(ip)) return $"http://{ip}:8428";
        }
        catch { /* fall through */ }
        return "http://127.0.0.1:8428";
    }

    private static string Trunc(string? s, int max = 400)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
