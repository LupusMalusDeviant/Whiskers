using Whiskers.Services.Certificates;
using System.Net;
using System.Text;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Automates integrating a freshly-added server into the mesh + mTLS fleet: installs Tailscale
/// (surfacing the interactive login link to the UI), deploys node_exporter, issues a per-host
/// server certificate from step-ca, deploys docker-socket-proxy + ghostunnel (mTLS), wires the host
/// into the VictoriaMetrics scrape config, and switches the server to TCP+mTLS + Prometheus metrics.
///
/// Every command runs through <see cref="IHostCommandExecutor"/>: on the NEW host over the SSH
/// bootstrap connection, and on "local" (the controller host) for step-ca and the scrape config.
/// Progress is reported as plain strings; the Tailscale login URL is reported with the
/// <see cref="LinkMarker"/> prefix so the UI can render it as a clickable link.
///
/// W3: the run is tracked per <see cref="OnboardingStep"/> and returns an <see cref="OnboardingResult"/>
/// with the failed step and an actionable hint. Every step is idempotent (existing installs are
/// detected and skipped), so a re-run after any failure resumes safely. Command strings are built by
/// <see cref="OnboardingCommands"/> (unit-tested; user input only ever passes through
/// <see cref="OnboardingCommands.Slug"/> or base64).
/// </summary>
public class OnboardingService : IOnboardingService
{
    public const string LinkMarker = "::TS_LOGIN::";

    private readonly IHostCommandExecutor _exec;
    private readonly IServerConfigService _serverConfig;
    private readonly IDockerService _docker;
    private readonly ILogger<OnboardingService> _logger;
    private readonly IMtlsCertificateService _certificates;
    private readonly IMtlsClientCredentialStore _clientCredentials;

    // Deployment-specific paths (match this Whiskers install). Since R1-44 each host gets its own client
    // credential from IMtlsClientCredentialStore ({MtlsDir}/hosts/{id}/); the CA bundle stays {MtlsDir}/ca.crt.
    // The per-host SERVER cert is issued on the host itself.
    private const string DockerProxyPort = "2376";
    private const string VmComposeDirOnHost = "/opt/telemetry-vm";   // holds scrape.yml + compose
    private const string ScrapeFileOnHost = "/opt/telemetry-vm/scrape.yml";

    public OnboardingService(
        IHostCommandExecutor exec,
        IServerConfigService serverConfig,
        IDockerService docker,
        IMtlsCertificateService certificates,
        IMtlsClientCredentialStore clientCredentials,
        ILogger<OnboardingService> logger,
        IMtlsClientKeyLoader? keyLoader = null)
    {
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
    }

    public async Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, IProgress<string> progress,
        CancellationToken ct = default)
    {
        var completed = new List<OnboardingStep>();
        var step = OnboardingStep.Bootstrap;

        // INV-59 (#187): exactly the server the decision named, by its id — never another server that a display name
        // or an id in another case would select.
        var server = Exact(serverId)?.Clone();   // edited below, saved once at the switchover
        if (server == null)
        {
            progress.Report("❌ Server nicht gefunden.");
            return OnboardingResult.Fail(completed, step, "Server nicht gefunden.");
        }

        var slug = OnboardingCommands.Slug(server.Name);
        SemaphoreSlim? hostLock = null;
        try
        {
            // R1-44 (contract 0007, amendment 10; review r3 MAJOR 1) and INV-59 (#187, contract 0012 A12): the whole run
            // holds the store's per-host lock, the one every certificate step and server removal take. A running
            // migration, renewal, rotation or removal makes onboarding refuse instead of interleaving slots, and while
            // the run holds it the server cannot be removed, so its id cannot come to select another server.
            var gate = _clientCredentials.HostLock(serverId);
            if (!await gate.WaitAsync(0, ct))
                return Fail(progress, completed, step,
                    $"Für {server.Name} läuft gerade ein Zertifikatsschritt (Migration, Erneuerung, Rotation oder Entfernen); " +
                    "Onboarding erneut starten, sobald er fertig ist.");
            hostLock = gate;
            // INV-59 (#187): admitted as a first onboarding, but the host holds mTLS state now (another onboarding
            // finished since the decision): replacing it needs the control-plane approval, so nothing runs.
            if (!mayReplaceMtlsState && MtlsOnboardingState.Holds(server, _clientCredentials))
                return Fail(progress, completed, step,
                    $"{server.Name} hat inzwischen mTLS-Zustand (ein anderes Onboarding ist fertig geworden). " +
                    "Erneutes Onboarding braucht die Freigabe eines Admins; bitte neu starten.");

            // 0) Bootstrap reachability ------------------------------------------------------------
            step = OnboardingStep.Bootstrap;
            progress.Report("① Bootstrap-Verbindung prüfen…");
            await Sh(serverId, "echo ok", TimeSpan.FromSeconds(20), ct);
            completed.Add(step);

            // 1) Install + start Tailscale ---------------------------------------------------------
            step = OnboardingStep.TailscaleInstall;
            progress.Report("② Tailscale installieren…");
            await Sh(serverId, "command -v tailscale >/dev/null || curl -fsSL https://tailscale.com/install.sh | sudo sh", TimeSpan.FromMinutes(3), ct);
            await ShTry(serverId, "sudo systemctl enable --now tailscaled", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 2) Bring up Tailscale. If the node is already authenticated (e.g. a re-run after a
            // failed onboarding), skip the login dance entirely and reuse the existing tailnet IP —
            // this is what makes a re-run a RESUME.
            step = OnboardingStep.TailscaleLogin;
            string? tsIp = null;
            var already = await Sh(serverId, "tailscale status --json 2>/dev/null | grep -m1 '\"BackendState\"'", TimeSpan.FromSeconds(20), ct);
            if (already.Output.Contains("Running"))
            {
                var ip0 = await Sh(serverId, "tailscale ip -4 2>/dev/null | head -1", TimeSpan.FromSeconds(20), ct);
                tsIp = string.IsNullOrWhiteSpace(ip0.Output) ? null : ip0.Output.Trim();
                if (tsIp != null) progress.Report("③ Node ist bereits im Tailnet — Login übersprungen (Resume).");
            }

            if (tsIp == null)
            {
                progress.Report("③ Tailscale starten…");
                await ShTry(serverId, "sudo systemctl reset-failed ts-up", TimeSpan.FromSeconds(10), ct);
                await Sh(serverId, OnboardingCommands.TailscaleUp(slug), TimeSpan.FromSeconds(20), ct);

                progress.Report("④ Auf Tailscale-Login warten…");
                var loginUrl = await PollAsync(async () =>
                {
                    var r = await Sh(serverId, "sudo journalctl -u tailscaled --since '90 seconds ago' --no-pager 2>/dev/null | grep -oE 'https://login\\.tailscale\\.com/[A-Za-z0-9/]+' | tail -1", TimeSpan.FromSeconds(20), ct);
                    var url = r.Output.Trim();
                    return string.IsNullOrEmpty(url) ? null : url;
                }, attempts: 15, delay: TimeSpan.FromSeconds(3), ct);

                if (loginUrl == null)
                {
                    progress.Report("❌ Kein Tailscale-Login-Link gefunden (Timeout).");
                    return Fail(progress, completed, step, "Kein Tailscale-Login-Link gefunden (Timeout).");
                }
                progress.Report($"{LinkMarker}{loginUrl}");
                progress.Report("⏳ Bitte den Link öffnen und den Node im Browser bestätigen…");

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
                    progress.Report("❌ Node nicht verbunden (Timeout beim Login).");
                    return Fail(progress, completed, step, "Node nicht verbunden (Timeout beim Login).");
                }
            }

            // The tailnet IP flows into shell commands and compose files below — accept only a real
            // IP literal, never arbitrary command output.
            if (!IPAddress.TryParse(tsIp, out _))
            {
                progress.Report($"❌ Unerwartete Tailscale-IP: '{tsIp}'");
                return Fail(progress, completed, step, $"Unerwartete Tailscale-IP: '{tsIp}'");
            }
            completed.Add(step);
            progress.Report($"✅ Mit dem Tailnet verbunden: {tsIp}");

            // 3) Ensure Docker is present — a fresh VPS may not have it; the telemetry + proxy steps
            // below all run `docker compose`. Install via the official convenience script if missing.
            step = OnboardingStep.Docker;
            progress.Report("⑤ Docker sicherstellen…");
            await Sh(serverId, "command -v docker >/dev/null 2>&1 || (curl -fsSL https://get.docker.com | sudo sh)", TimeSpan.FromMinutes(5), ct);
            await ShTry(serverId, "sudo systemctl enable --now docker", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 4) Deploy node_exporter (mesh-only) on the new host ---------------------------------
            step = OnboardingStep.NodeExporter;
            progress.Report("⑥ node-exporter deployen…");
            await WriteFile(serverId, "/opt/telemetry/docker-compose.yml", OnboardingCommands.NodeExporterCompose(tsIp), ct);
            await Sh(serverId, "cd /opt/telemetry && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);
            completed.Add(step);

            // 5) Issue the per-host server cert: the key is generated ON the host and never leaves it; only
            // the CSR travels to step-ca (on local) and the signed certificate travels back. It used to be the
            // other way round — key made on Badwolf, shipped base64 through the command channel.
            step = OnboardingStep.Certificate;
            // Steps 0–4 can take minutes (Tailscale login): continue with the record as it is now, so a change finished
            // meanwhile (an edit) is not overwritten at the switchover (review r4 MINOR 2).
            if (Exact(serverId)?.Clone() is not { } current)
                return Fail(progress, completed, step, $"{server.Name} wurde inzwischen entfernt.");
            server = current;
            if (!mayReplaceMtlsState && MtlsOnboardingState.Holds(server, _clientCredentials))
                return Fail(progress, completed, step,
                    $"{server.Name} hat inzwischen mTLS-Zustand (ein anderes Onboarding ist fertig geworden). " +
                    "Erneutes Onboarding braucht die Freigabe eines Admins; bitte neu starten.");
            progress.Report("⑥ Server-Zertifikat ausstellen (Schlüssel auf dem Host, step-ca signiert)…");
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

            // 6) Deploy socket-proxy + ghostunnel (mTLS) on the new host -------------------------
            step = OnboardingStep.MtlsProxy;
            progress.Report("⑦ socket-proxy + ghostunnel (mTLS) deployen…");
            await WriteFile(serverId, "/opt/dockerproxy/docker-compose.yml",
                OnboardingCommands.DockerProxyCompose(tsIp, clientIdentity.CommonName), ct);
            await Sh(serverId, "cd /opt/dockerproxy && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);
            completed.Add(step);

            // 7) Wire into the VictoriaMetrics scrape config (on local) + reload -----------------
            step = OnboardingStep.ScrapeConfig;
            progress.Report("⑧ In Scrape-Config eintragen…");
            await Sh("local", OnboardingCommands.AddScrapeTargetCommand(ScrapeFileOnHost, serverId, tsIp), TimeSpan.FromSeconds(20), ct);
            await ShTry("local", $"cd {VmComposeDirOnHost} && docker compose restart victoriametrics", TimeSpan.FromSeconds(30), ct);
            completed.Add(step);

            // 8) Switch the server to TCP + mTLS + Prometheus ------------------------------------
            step = OnboardingStep.Switchover;
            progress.Report("⑨ Server auf TCP+mTLS umstellen…");
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

            // 9) Verify over mTLS -----------------------------------------------------------------
            step = OnboardingStep.Verify;
            progress.Report("⑩ Verifizieren (mTLS)…");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var info = await _docker.GetServerSystemInfoAsync(serverId);
            if (!info.IsReachable)
            {
                progress.Report($"⚠️ Onboarding fertig, aber mTLS-Verbindung noch nicht erreichbar: {info.Error}");
                return Fail(progress, completed, step, info.Error ?? "mTLS-Verbindung nicht erreichbar.");
            }
            // The issued slot was pending; the record names it and the connection through it works: now it is active.
            if (_clientCredentials.PendingFor(serverId) is { } pendingSlot
                && MtlsClientCredentialStore.SamePath(server.TcpClientCertPath, pendingSlot.Cert))
                await _clientCredentials.ActivatePendingAsync(serverId, CancellationToken.None);
            await RecordClientIdentityAsync(server, clientIdentity, progress, ct);
            completed.Add(step);

            // Onboarding succeeded over mTLS → drop the one-time bootstrap credentials so nothing
            // standing remains: clear the in-memory password and delete the SSH key from disk.
            server.SshPassword = null;
            await _serverConfig.DeleteSshKeyAsync(serverId);

            progress.Report($"🎉 Fertig! {server.Name} ist im Mesh, läuft über mTLS ({info.ContainersRunning}/{info.ContainersTotal} Container, {info.OperatingSystem}). SSH-Bootstrap-Key + Passwort wurden entfernt — SSH wird nicht mehr benötigt.");
            return OnboardingResult.Ok(completed);
        }
        catch (OperationCanceledException)
        {
            progress.Report("⏹️ Abgebrochen. Erneutes Starten setzt am unterbrochenen Schritt wieder auf (alle Schritte sind idempotent).");
            return OnboardingResult.Fail(completed, step, "Abgebrochen.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding failed for {ServerId} at step {Step}", serverId, step);
            return Fail(progress, completed, step, ex.Message);
        }
        finally
        {
            hostLock?.Release();
        }
    }

    /// <summary>Seam for tests: a probe of one client credential against the new host (one-off connection).</summary>
    internal Func<Models.ServerConfig, CancellationToken, Task<MtlsProbeOutcome>> ProbeCredential { get; set; }

    /// <summary>
    /// R1-44 (D6, amendments 2 and 3): the phase-5-equivalent record of a new host. Its proxy was written with only the
    /// host's own name; the record says so only once the shared credential was refused there with a TLS alert — or when
    /// there is no shared credential to try (fresh install, retired). Otherwise it stays at phase 4 and the page says why.
    /// </summary>
    private async Task RecordClientIdentityAsync(Models.ServerConfig server, MtlsClientIdentity identity, IProgress<string> progress, CancellationToken ct)
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
                progress.Report("⚠️ Ob der Host die gemeinsame Client-Identität ablehnt, ließ sich nicht prüfen. Die Migration " +
                                "(„Migrieren“) wiederholt die Prüfung.");
        }
        await _clientCredentials.WriteJournalAsync(server.Id, new MtlsRolloutJournal(MtlsRolloutJournal.Onboard,
            identity.CommonName, identity.Generation, sharedExists && !refused ? 4 : 5,
            sharedExists ? MtlsCertificateService.CommonNameOf(shared.Cert) : null, refused, DateTime.UtcNow,
            SharedCredentialAbsent: !sharedExists), ct);
    }

    /// <summary>Reports the per-step hint + technical detail and builds the failure result.</summary>
    private static OnboardingResult Fail(IProgress<string> progress, List<OnboardingStep> completed, OnboardingStep step, string detail)
    {
        progress.Report($"❌ {OnboardingResult.Hint(step)}");
        progress.Report("↻ Erneutes Starten des Onboardings ist gefahrlos — abgeschlossene Schritte werden erkannt und übersprungen.");
        return OnboardingResult.Fail(completed, step, $"{OnboardingResult.Hint(step)} (Details: {Trunc(detail)})");
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The server whose id is exactly <paramref name="serverId"/> (INV-59, #187): <c>GetServer</c> also matches an
    /// id in another case and a display name, which could select another server once this one is gone.</summary>
    private Models.ServerConfig? Exact(string serverId) =>
        _serverConfig.GetServer(serverId) is { } s && string.Equals(s.Id, serverId, StringComparison.Ordinal) ? s : null;

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
