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

    // Deployment-specific paths (match this Whiskers install). The shared client cert + CA that
    // every managed host's ghostunnel is verified against live here (created during the first
    // migration); only the per-host SERVER cert is issued anew.
    private const string StepCaContainer = "step-ca";
    private const string DockerProxyPort = "2376";
    private readonly string _mtlsCertDir; // /app/data/mtls by default: client.crt/key + ca.crt (shared)
    private const string VmComposeDirOnHost = "/opt/telemetry-vm";   // holds scrape.yml + compose
    private const string ScrapeFileOnHost = "/opt/telemetry-vm/scrape.yml";

    public OnboardingService(
        IHostCommandExecutor exec,
        IServerConfigService serverConfig,
        IDockerService docker,
        ILogger<OnboardingService> logger,
        DataPathOptions? dataPaths = null)
    {
        _exec = exec;
        _serverConfig = serverConfig;
        _docker = docker;
        _logger = logger;
        _mtlsCertDir = (dataPaths ?? DataPathOptions.Default).MtlsDir;
    }

    public async Task<OnboardingResult> OnboardServerAsync(string serverId, IProgress<string> progress, CancellationToken ct = default)
    {
        var completed = new List<OnboardingStep>();
        var step = OnboardingStep.Bootstrap;

        var server = _serverConfig.GetServer(serverId);
        if (server == null)
        {
            progress.Report("❌ Server nicht gefunden.");
            return OnboardingResult.Fail(completed, step, "Server nicht gefunden.");
        }

        var slug = OnboardingCommands.Slug(server.Name);
        try
        {
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

            // 5) Issue the per-host server cert from step-ca (on local) ---------------------------
            step = OnboardingStep.Certificate;
            progress.Report("⑥ Server-Zertifikat ausstellen (step-ca)…");
            await Sh("local", OnboardingCommands.CertCreate(StepCaContainer, slug, tsIp), TimeSpan.FromSeconds(60), ct);

            var serverCrtB64 = (await Sh("local", $"base64 -w0 /opt/step-ca/certs/{slug}-server.crt", TimeSpan.FromSeconds(20), ct)).Output.Trim();
            var serverKeyB64 = (await Sh("local", $"base64 -w0 /opt/step-ca/secrets/{slug}-server.key", TimeSpan.FromSeconds(20), ct)).Output.Trim();
            // CA bundle = root + intermediate (ghostunnel must complete the chain for the leaf-only .NET client)
            var caBundleB64 = (await Sh("local", "cat /opt/step-ca/certs/root_ca.crt /opt/step-ca/certs/intermediate_ca.crt | base64 -w0", TimeSpan.FromSeconds(20), ct)).Output.Trim();
            completed.Add(step);

            // 6) Deploy socket-proxy + ghostunnel (mTLS) on the new host -------------------------
            step = OnboardingStep.MtlsProxy;
            progress.Report("⑦ socket-proxy + ghostunnel (mTLS) deployen…");
            await Sh(serverId, "sudo mkdir -p /opt/dockerproxy/certs", TimeSpan.FromSeconds(20), ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/server.crt", serverCrtB64, ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/server.key", serverKeyB64, ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/ca.crt", caBundleB64, ct);
            await Sh(serverId, "sudo chmod 600 /opt/dockerproxy/certs/server.key", TimeSpan.FromSeconds(20), ct);
            await WriteFile(serverId, "/opt/dockerproxy/docker-compose.yml", OnboardingCommands.DockerProxyCompose(tsIp), ct);
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
            server.TcpClientCertPath = $"{_mtlsCertDir}/client.crt";
            server.TcpClientKeyPath = $"{_mtlsCertDir}/client.key";
            server.TcpCaCertPath = $"{_mtlsCertDir}/ca.crt";
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
    }

    /// <summary>Reports the per-step hint + technical detail and builds the failure result.</summary>
    private static OnboardingResult Fail(IProgress<string> progress, List<OnboardingStep> completed, OnboardingStep step, string detail)
    {
        progress.Report($"❌ {OnboardingResult.Hint(step)}");
        progress.Report("↻ Erneutes Starten des Onboardings ist gefahrlos — abgeschlossene Schritte werden erkannt und übersprungen.");
        return OnboardingResult.Fail(completed, step, $"{OnboardingResult.Hint(step)} (Details: {Trunc(detail)})");
    }

    // ---- helpers -------------------------------------------------------------------------------

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
