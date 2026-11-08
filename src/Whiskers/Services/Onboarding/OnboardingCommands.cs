using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Onboarding;

/// <summary>Pure command/config builders for the onboarding flow, extracted from
/// <see cref="OnboardingService"/> so command construction is unit-testable with plain string
/// assertions (project rule: everything that turns strings into shell commands gets
/// command-building tests). All host-bound values pass through <see cref="Slug"/> (strict
/// character allow-list) or are transported base64-encoded — never raw user input in a shell line.</summary>
public static class OnboardingCommands
{
    /// <summary>Lowercase DNS-safe slug from a display name: anything outside [a-z0-9] collapses
    /// to a single hyphen; never empty. The ONLY sanitizer between a user-chosen server name and
    /// the shell (hostname, cert paths) — keep it an allow-list.</summary>
    public static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "server" : s;
    }

    public static string TailscaleUp(string slug) =>
        $"sudo systemd-run --collect --unit=ts-up tailscale up --accept-dns=false --hostname={slug}";

    /// <summary>Writes a file on the target by piping base64 through <c>sudo tee</c> — content
    /// never touches shell quoting. The directory part must be a fixed, code-owned path.</summary>
    public static string WriteFileB64(string path, string b64)
    {
        var dir = path[..path.LastIndexOf('/')];
        return $"sudo mkdir -p {dir} && echo {b64} | base64 -d | sudo tee {path} >/dev/null";
    }

    /// <summary>The regenerate-safe scrape-config append, shipped as base64-encoded python so the
    /// YAML content and IP/serverId values never meet the shell.</summary>
    public static string AddScrapeTargetCommand(string scrapeFileOnHost, string serverId, string tsIp)
    {
        var py =
            "import sys\n" +
            $"f='{scrapeFileOnHost}'\n" +
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
        return $"echo {b64} | base64 -d | python3 -";
    }

    // Host files are LF: normalise, so a CRLF checkout of this source cannot put CR characters into them.
    public static string NodeExporterCompose(string tsIp) => NodeExporterComposeFor(tsIp).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string NodeExporterComposeFor(string tsIp) =>
$@"services:
  node-exporter:
    image: quay.io/prometheus/node-exporter:v1.12.1
    container_name: node-exporter
    restart: unless-stopped
    network_mode: host
    pid: host
    command:
      - '--path.rootfs=/host'
      - '--web.listen-address={tsIp}:9100'
      - '--collector.textfile.directory=/host/var/lib/node_exporter/textfile_collector'
    volumes:
      - '/:/host:ro,rslave'
";

    /// <summary>The mTLS proxy of a managed host: docker-socket-proxy behind ghostunnel. Since R1-44 ghostunnel admits
    /// exactly one client name, the host's own (<paramref name="allowedClientCn"/>, allow-listed); every other flag is the
    /// approved one (<see cref="Whiskers.Services.Certificates.MtlsAllowListCommands.ApprovedArgsWithoutAllowCn"/>).</summary>
    public static string DockerProxyCompose(string tsIp, string allowedClientCn) =>
        Whiskers.Services.Certificates.MtlsClientIdentity.IsValidCommonName(allowedClientCn)
            ? DockerProxyComposeFor(tsIp, allowedClientCn).Replace("\r\n", "\n", StringComparison.Ordinal)
            : throw new ArgumentException($"'{allowedClientCn}' is not an allowed client name.", nameof(allowedClientCn));

    private static string DockerProxyComposeFor(string tsIp, string allowedClientCn) =>
$@"services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:v0.5.0@sha256:1f5038b54f06c3e18422902cf00ba21803d1c97805aae032e5e6673d532d3459
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
    image: ghostunnel/ghostunnel:v1.11.5@sha256:65c4e34ec0ee0f30ea1f72b7411ec70a91c32fc50f067a8466ad2adb1e7420f2
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
      - --allow-cn={allowedClientCn}
    volumes:
      - /opt/dockerproxy/certs:/certs:ro
    ports:
      - '{tsIp}:2376:2376'
    depends_on:
      - socket-proxy
";
}
