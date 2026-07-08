using System.Text;
using System.Text.RegularExpressions;
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
/// bootstrap connection, and on "local" (the controller host) for step-ca and the scrape config. Progress is
/// reported as plain strings; the Tailscale login URL is reported with the <see cref="LinkMarker"/>
/// prefix so the UI can render it as a clickable link.
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
    private const string MtlsCertDirInContainer = "/app/data/mtls"; // client.crt/key + ca.crt (shared)
    private const string VmComposeDirOnHost = "/opt/telemetry-vm";   // holds scrape.yml + compose
    private const string ScrapeFileOnHost = "/opt/telemetry-vm/scrape.yml";

    public OnboardingService(
        IHostCommandExecutor exec,
        IServerConfigService serverConfig,
        IDockerService docker,
        ILogger<OnboardingService> logger)
    {
        _exec = exec;
        _serverConfig = serverConfig;
        _docker = docker;
        _logger = logger;
    }

    public async Task<bool> OnboardServerAsync(string serverId, IProgress<string> progress, CancellationToken ct = default)
    {
        var server = _serverConfig.GetServer(serverId);
        if (server == null) { progress.Report("❌ Server nicht gefunden."); return false; }

        var slug = Slug(server.Name);
        try
        {
            // 0) Bootstrap reachability ------------------------------------------------------------
            progress.Report("① Bootstrap-Verbindung prüfen…");
            await Sh(serverId, "echo ok", TimeSpan.FromSeconds(20), ct);

            // 1) Install + start Tailscale ---------------------------------------------------------
            progress.Report("② Tailscale installieren…");
            await Sh(serverId, "command -v tailscale >/dev/null || curl -fsSL https://tailscale.com/install.sh | sudo sh", TimeSpan.FromMinutes(3), ct);
            await ShTry(serverId, "sudo systemctl enable --now tailscaled", TimeSpan.FromSeconds(30), ct);

            // 2) Bring up Tailscale. If the node is already authenticated (e.g. a re-run after a
            // failed onboarding), skip the login dance entirely and reuse the existing tailnet IP.
            string? tsIp = null;
            var already = await Sh(serverId, "tailscale status --json 2>/dev/null | grep -m1 '\"BackendState\"'", TimeSpan.FromSeconds(20), ct);
            if (already.Output.Contains("Running"))
            {
                var ip0 = await Sh(serverId, "tailscale ip -4 2>/dev/null | head -1", TimeSpan.FromSeconds(20), ct);
                tsIp = string.IsNullOrWhiteSpace(ip0.Output) ? null : ip0.Output.Trim();
            }

            if (tsIp == null)
            {
                progress.Report("③ Tailscale starten…");
                await ShTry(serverId, "sudo systemctl reset-failed ts-up", TimeSpan.FromSeconds(10), ct);
                await Sh(serverId, $"sudo systemd-run --collect --unit=ts-up tailscale up --accept-dns=false --hostname={slug}", TimeSpan.FromSeconds(20), ct);

                progress.Report("④ Auf Tailscale-Login warten…");
                var loginUrl = await PollAsync(async () =>
                {
                    var r = await Sh(serverId, "sudo journalctl -u tailscaled --since '90 seconds ago' --no-pager 2>/dev/null | grep -oE 'https://login\\.tailscale\\.com/[A-Za-z0-9/]+' | tail -1", TimeSpan.FromSeconds(20), ct);
                    var url = r.Output.Trim();
                    return string.IsNullOrEmpty(url) ? null : url;
                }, attempts: 15, delay: TimeSpan.FromSeconds(3), ct);

                if (loginUrl == null) { progress.Report("❌ Kein Tailscale-Login-Link gefunden (Timeout)."); return false; }
                progress.Report($"{LinkMarker}{loginUrl}");
                progress.Report("⏳ Bitte den Link öffnen und den Node im Browser bestätigen…");

                // 3) Wait until the node is authenticated + has an IP ----------------------------------
                tsIp = await PollAsync(async () =>
                {
                    var st = await Sh(serverId, "tailscale status --json 2>/dev/null | grep -m1 '\"BackendState\"'", TimeSpan.FromSeconds(20), ct);
                    if (!st.Output.Contains("Running")) return null;
                    var ip = await Sh(serverId, "tailscale ip -4 2>/dev/null | head -1", TimeSpan.FromSeconds(20), ct);
                    var v = ip.Output.Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }, attempts: 100, delay: TimeSpan.FromSeconds(3), ct); // ~5 min for the user to click

                if (tsIp == null) { progress.Report("❌ Node nicht verbunden (Timeout beim Login)."); return false; }
            }
            progress.Report($"✅ Mit dem Tailnet verbunden: {tsIp}");

            // 3b) Ensure Docker is present — a fresh VPS may not have it; the telemetry + proxy steps
            // below all run `docker compose`. Install via the official convenience script if missing.
            progress.Report("⑤ Docker sicherstellen…");
            await Sh(serverId, "command -v docker >/dev/null 2>&1 || (curl -fsSL https://get.docker.com | sudo sh)", TimeSpan.FromMinutes(5), ct);
            await ShTry(serverId, "sudo systemctl enable --now docker", TimeSpan.FromSeconds(30), ct);

            // 4) Deploy node_exporter (mesh-only) on the new host ---------------------------------
            progress.Report("⑥ node-exporter deployen…");
            await WriteFile(serverId, "/opt/telemetry/docker-compose.yml", NodeExporterCompose(tsIp), ct);
            await Sh(serverId, "cd /opt/telemetry && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);

            // 5) Issue the per-host server cert from step-ca (on local) ---------------------------
            progress.Report("⑥ Server-Zertifikat ausstellen (step-ca)…");
            var caPwFile = "/home/step/secrets/password";
            await Sh("local", $"docker exec {StepCaContainer} step certificate create {slug}-dockerproxy " +
                $"/home/step/certs/{slug}-server.crt /home/step/secrets/{slug}-server.key " +
                $"--ca /home/step/certs/intermediate_ca.crt --ca-key /home/step/secrets/intermediate_ca_key " +
                $"--ca-password-file {caPwFile} --san {tsIp} --san {slug} --not-after 8760h --no-password --insecure --bundle --force",
                TimeSpan.FromSeconds(60), ct);

            var serverCrtB64 = (await Sh("local", $"base64 -w0 /opt/step-ca/certs/{slug}-server.crt", TimeSpan.FromSeconds(20), ct)).Output.Trim();
            var serverKeyB64 = (await Sh("local", $"base64 -w0 /opt/step-ca/secrets/{slug}-server.key", TimeSpan.FromSeconds(20), ct)).Output.Trim();
            // CA bundle = root + intermediate (ghostunnel must complete the chain for the leaf-only .NET client)
            var caBundleB64 = (await Sh("local", "cat /opt/step-ca/certs/root_ca.crt /opt/step-ca/certs/intermediate_ca.crt | base64 -w0", TimeSpan.FromSeconds(20), ct)).Output.Trim();

            // 6) Deploy socket-proxy + ghostunnel (mTLS) on the new host -------------------------
            progress.Report("⑦ socket-proxy + ghostunnel (mTLS) deployen…");
            await Sh(serverId, "sudo mkdir -p /opt/dockerproxy/certs", TimeSpan.FromSeconds(20), ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/server.crt", serverCrtB64, ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/server.key", serverKeyB64, ct);
            await WriteFileB64(serverId, "/opt/dockerproxy/certs/ca.crt", caBundleB64, ct);
            await Sh(serverId, "sudo chmod 600 /opt/dockerproxy/certs/server.key", TimeSpan.FromSeconds(20), ct);
            await WriteFile(serverId, "/opt/dockerproxy/docker-compose.yml", DockerProxyCompose(tsIp), ct);
            await Sh(serverId, "cd /opt/dockerproxy && sudo docker compose up -d", TimeSpan.FromMinutes(2), ct);

            // 7) Wire into the VictoriaMetrics scrape config (on local) + reload -----------------
            progress.Report("⑧ In Scrape-Config eintragen…");
            await AddScrapeTarget(serverId, tsIp, ct);
            await ShTry("local", $"cd {VmComposeDirOnHost} && docker compose restart victoriametrics", TimeSpan.FromSeconds(30), ct);

            // 8) Switch the server to TCP + mTLS + Prometheus ------------------------------------
            progress.Report("⑨ Server auf TCP+mTLS umstellen…");
            var vmEndpoint = await ResolveVmEndpoint(ct);
            server.ConnectionType = ConnectionType.TCP;
            server.TcpHost = tsIp;
            server.TcpPort = int.Parse(DockerProxyPort);
            server.TcpUseTls = true;
            server.TcpClientCertPath = $"{MtlsCertDirInContainer}/client.crt";
            server.TcpClientKeyPath = $"{MtlsCertDirInContainer}/client.key";
            server.TcpCaCertPath = $"{MtlsCertDirInContainer}/ca.crt";
            server.MetricsSource = MetricsSourceKind.Prometheus;
            server.MetricsEndpoint = vmEndpoint;
            await _serverConfig.UpdateServerAsync(server);

            // 9) Verify over mTLS -----------------------------------------------------------------
            progress.Report("⑩ Verifizieren (mTLS)…");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var info = await _docker.GetServerSystemInfoAsync(serverId);
            if (!info.IsReachable)
            {
                progress.Report($"⚠️ Onboarding fertig, aber mTLS-Verbindung noch nicht erreichbar: {info.Error}");
                return false;
            }

            // Onboarding succeeded over mTLS → drop the one-time bootstrap credentials so nothing
            // standing remains: clear the in-memory password and delete the SSH key from disk.
            server.SshPassword = null;
            await _serverConfig.DeleteSshKeyAsync(serverId);

            progress.Report($"🎉 Fertig! {server.Name} ist im Mesh, läuft über mTLS ({info.ContainersRunning}/{info.ContainersTotal} Container, {info.OperatingSystem}). SSH-Bootstrap-Key + Passwort wurden entfernt — SSH wird nicht mehr benötigt.");
            return true;
        }
        catch (OperationCanceledException)
        {
            progress.Report("⏹️ Abgebrochen.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding failed for {ServerId}", serverId);
            progress.Report($"❌ Fehler: {ex.Message}");
            return false;
        }
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
    {
        var dir = path[..path.LastIndexOf('/')];
        await Sh(serverId, $"sudo mkdir -p {dir} && echo {b64} | base64 -d | sudo tee {path} >/dev/null", TimeSpan.FromSeconds(30), ct);
    }

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

    // Regenerate-safe append: add this host's node target to the scrape config if absent.
    private async Task AddScrapeTarget(string serverId, string tsIp, CancellationToken ct)
    {
        var py =
            "import sys\n" +
            $"f='{ScrapeFileOnHost}'\n" +
            "import re\n" +
            "s=open(f).read()\n" +
            $"ip='{tsIp}'; sid='{serverId}'\n" +
            "if ip in s:\n    print('already present'); sys.exit(0)\n" +
            "lines=s.rstrip().split(chr(10))\n" +
            "out=[]\n" +
            "ins=False\n" +
            "for ln in lines:\n" +
            "    out.append(ln)\n" +
            "    if not ins and ln.strip()=='static_configs:':\n" +
            "        out.append(\"      - targets: ['%s:9100']\" % ip)\n" +
            "        out.append(\"        labels: { server: '%s' }\" % sid)\n" +
            "        ins=True\n" +
            "open(f,'w').write(chr(10).join(out)+chr(10))\n" +
            "print('added' if ins else 'no static_configs anchor found')\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(py));
        await Sh("local", $"echo {b64} | base64 -d | python3 -", TimeSpan.FromSeconds(20), ct);
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

    private static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "server" : s;
    }

    private static string Trunc(string? s, int max = 400)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string NodeExporterCompose(string tsIp) =>
$@"services:
  node-exporter:
    image: quay.io/prometheus/node-exporter:v1.8.2
    container_name: node-exporter
    restart: unless-stopped
    network_mode: host
    pid: host
    command:
      - '--path.rootfs=/host'
      - '--web.listen-address={tsIp}:9100'
    volumes:
      - '/:/host:ro,rslave'
";

    private static string DockerProxyCompose(string tsIp) =>
$@"services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:latest
    container_name: socket-proxy
    restart: unless-stopped
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      INFO: 1
      VERSION: 1
      POST: 1
      EXEC: 0
      SWARM: 0
      SECRETS: 0
      CONFIGS: 0
      PLUGINS: 0
      SYSTEM: 0
      AUTH: 0
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
  ghostunnel:
    image: ghostunnel/ghostunnel:v1.8.2
    container_name: ghostunnel
    restart: unless-stopped
    command:
      - server
      - --listen=0.0.0.0:2376
      - --target=socket-proxy:2375
      - --unsafe-target
      - --cert=/certs/server.crt
      - --key=/certs/server.key
      - --cacert=/certs/ca.crt
      - --allow-cn=serverwatch-client
    volumes:
      - /opt/dockerproxy/certs:/certs:ro
    ports:
      - '{tsIp}:2376:2376'
    depends_on:
      - socket-proxy
";
}
