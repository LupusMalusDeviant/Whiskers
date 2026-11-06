# Installation

All install paths. The short version is in the [README](../README.md#install).

Prebuilt multi-arch images (amd64 / arm64) are published to `ghcr.io/lupusmalusdeviant/whiskers` from v0.12.0. For
older versions use [From source](#from-source-development). This page describes the releases after 0.15.0 (`main`).

## Installer

```bash
VER=X.Y.Z   # a release from https://github.com/LupusMalusDeviant/Whiskers/releases
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/install.sh"
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/checksums.txt"
sha256sum --check --ignore-missing checksums.txt && bash install.sh
```

Every installer writes `./whiskers/`, starts the container and waits until the app is healthy. The checksum catches a
broken download. The rest depends on the release:

**Installers of releases after 0.15.0:**

- install exactly that release, never `latest`;
- generate the vault key on a new install (`VAULT_KEY_FILE`; an upgrade of an older install only warns) and print where it is and what to back up. Keep the key and the backups
  apart, off the host;
- verify the image signature if cosign is installed;
- flags: `--version`, `--image`, `--verify`, `--port`, `--bind`, `--data`, `--dir`, `--install-docker`, `--allow-unknown-current` (upgrade an older `latest` install), `--yes` (`install.sh --help`);
- re-running with a newer release upgrades in place (only the image line changes); downgrades are refused.

**Installers of 0.15.0 and older** default to `latest`, generate no vault key, verify no signature and print no setup
token; those images do not read `VAULT_KEY_FILE` either. Use the
[README at v0.15.0](https://github.com/LupusMalusDeviant/Whiskers/blob/v0.15.0/README.md) for them.

Details: [deploy/README.md → Installer](../deploy/README.md#installer).

## Plain `docker run`

No vault key — set `VAULT_KEY_FILE` before you store secrets:

```bash
VER=X.Y.Z
docker run -d --name whiskers -p 127.0.0.1:5100:8080 \
  -v whiskers-data:/app/data -v /var/run/docker.sock:/var/run/docker.sock \
  "ghcr.io/lupusmalusdeviant/whiskers:${VER}"
```

## Docker Compose

Download `docker-compose.yml` from the [latest release](https://github.com/LupusMalusDeviant/Whiskers/releases)
(already pinned to the image), then:

```bash
docker compose up -d
```

Optional settings go in a `.env` beside it; not every key is mapped (see [configuration.md](configuration.md)). The app
listens on `127.0.0.1:5100` by default (`HOST_BIND` / `HOST_PORT`) and binds publicly only if you change the bind.

## First sign-in

On first launch a setup wizard in the browser creates the admin account; no `.env` and no config file are required.
Whoever completes the wizard becomes Admin, so it first asks for a one-time setup token that only someone with access
to the server can read:

```bash
docker exec whiskers cat /app/data/setup-token.txt      # installer or docker run
docker exec serverwatch cat /app/data/setup-token.txt   # release docker-compose.yml (container keeps its pre-rebrand name)
```

- The installer prints the token. Helm users `kubectl exec` the same path, or preset it with `WHISKERS_SETUP_TOKEN`.
- After setup you sign in with the local login (email + password). 5 wrong passwords lock the account for 15 minutes;
  login attempts are rate-limited per client.
- Admins add, reset and delete local accounts under *Settings → Local accounts*; everyone changes their own password
  under *My account*.
- A forgotten password is recovered with a one-time code, from an Admin or on the host with
  `--recover-local-account` — see [Local accounts](operations/local-accounts.md).
- **Local accounts have no second factor (MFA) in 1.0.** For production, sign in through your identity provider
  (OIDC) and require MFA there; keep local accounts as a break-glass fallback.

Alternatives:

- Seed the admin unattended with `WHISKERS_ADMIN_EMAIL` + `WHISKERS_ADMIN_PASSWORD_FILE`.
- Use Google or generic OIDC ([Configuration](configuration.md)).
- For a trial on a trusted network, `AUTH_DISABLED=true` (container variable `Auth__Disabled`). Every request is then an Admin, so Whiskers only answers
  clients from private networks, with an IP or `localhost` as host name and without a foreign `Origin`.

## MCP clients

Whiskers is self-hosted: there is no hosted or cloud endpoint. Your MCP client talks to your own instance at `/mcp`
(Streamable HTTP) with an API key created under *Settings → MCP*. The snippets read the key from the environment, so
it never lands in a config file:

```bash
export WHISKERS_MCP_API_KEY=…
```

**Generic JSON config:**

```json
{
  "mcpServers": {
    "whiskers": {
      "type": "http",
      "url": "http://localhost:5100/mcp",
      "headers": { "Authorization": "Bearer <WHISKERS_MCP_API_KEY>" }
    }
  }
}
```

**Claude Code** — the single quotes keep `${WHISKERS_MCP_API_KEY}` literal in `.mcp.json`; Claude Code expands it when
it loads the file:

```bash
claude mcp add --transport http --scope project whiskers http://localhost:5100/mcp --header 'Authorization: Bearer ${WHISKERS_MCP_API_KEY}'
```

**Crush** ([charmbracelet/crush](https://github.com/charmbracelet/crush)) — add this line to `./crushrc` (project) or
`~/.config/crush/crushrc` (global); Crush expands `$WHISKERS_MCP_API_KEY` when it loads the config:

```
mcp add whiskers --type http --url "http://localhost:5100/mcp" --header Authorization "Bearer $WHISKERS_MCP_API_KEY"
```

The same entry in the legacy `crush.json`:

```json
{
  "$schema": "https://charm.land/crush.json",
  "mcp": {
    "whiskers": {
      "type": "http",
      "url": "http://localhost:5100/mcp",
      "headers": { "Authorization": "Bearer $WHISKERS_MCP_API_KEY" }
    }
  }
}
```

Use your instance's URL instead of `localhost:5100` when Whiskers runs elsewhere. Behind a reverse proxy on a subpath
(`PATH_BASE=/whiskers`) the URL becomes `https://example.com/whiskers/mcp`. Scopes, tools and guardrails:
[MCP server](mcp.md).

## Behind an Nginx reverse proxy

```nginx
location /whiskers/ {
    proxy_pass http://127.0.0.1:5100/whiskers/;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Prefix /whiskers;
    proxy_read_timeout 300s;
    proxy_buffer_size 128k;
    proxy_buffers 4 256k;
}
```

When serving under a subpath, set `PATH_BASE=/whiskers` in `.env` (release compose file) or the container variable `PathBase=/whiskers` (installer, `docker run`).

**Name your proxy.** Whiskers believes `X-Forwarded-For` / `X-Forwarded-Proto` only from loopback unless you list the
proxy: `ForwardedHeaders__TrustedNetworks__0=172.18.0.2/32` (a CIDR or a single address; more entries with `__1`, …).

- A proxy in another container or on another host that is not listed is ignored. Whiskers then sees the proxy's
  address for every visitor and plain HTTP behind a TLS-terminating proxy: the session cookie has no `Secure` flag,
  and Google/OIDC sign-in fails because the callback is built with `http://`. `AUTH_DISABLED=true` refuses the
  proxied requests, and *Settings → production readiness* names the proxy.
- An entry that is not a canonical CIDR or address stops the start. A single value without index
  (`ForwardedHeaders__TrustedNetworks=10.0.0.0/8,172.18.0.2`) is read as a comma-separated list.
- Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` instead: it trusts every peer, and Whiskers refuses to start
  with it.

## Tailscale Serve, Funnel and DockTail

It depends on the mode.

**HTTP mode.** tailscaled acts as a reverse proxy like any other. This covers `tailscale serve --http`/`--https`,
Tailscale Services served over HTTP(S), and DockTail's default `docktail.service.protocol=http`. tailscaled replaces
`X-Forwarded-For` with the caller's address; for Funnel traffic, that is the visitor's public address. List the address
tailscaled connects from in `ForwardedHeaders__TrustedNetworks__0`:

- **tailscaled on the same host as a native install, Serve target `localhost`:** loopback, which is already trusted.
  Keep the target on `localhost`: a target on another address of the host makes that address the peer, and listing it
  trusts everything that connects to it, any TCP forward to it included.
- **tailscaled on the host or with host networking, Whiskers in a container** (DockTail's default): the gateway of
  Whiskers' Docker network, for example `172.18.0.1/32`. Listing the gateway trusts everything that reaches Whiskers
  from it: host processes, published ports handled by docker-proxy (localhost, hairpin, rootless or userland-proxy
  setups), and any TCP forward. Do it only when the container publishes no ports and no TCP forward targets it.
- **The safer layout:** a Tailscale sidecar with `network_mode: service:tailscale`. tailscaled then connects over
  loopback and nothing needs to be listed.

**TCP mode.** This covers `--tcp`, `--tls-terminated-tcp` and `docktail.service.protocol=tcp`. tailscaled adds no
forwarded headers; it passes whatever the client sends through unchanged, `Host` and `X-Forwarded-*` included.
Whiskers sees tailscaled's address as the peer:

- **If that address is not trusted:** every visitor appears to come from that one address. Forwarded headers a client
  sends are ignored, and once a client sends them, the peer is named in production readiness until 24 hours after the
  last time.
  - All visitors share one login rate limit (10 attempts a minute), so a single client can use it up and keep everyone
    else from signing in locally.
  - The audit shows only that address.
  - Behind `--tls-terminated-tcp`, Whiskers treats the connection as plain HTTP: the session cookie has no `Secure`
    flag, and a Google/OIDC callback is built with `http://`.
- **If that address is trusted** (loopback for a forward to `localhost`, or a gateway you listed): the client picks its
  own address and scheme. That bypasses the per-client login rate limit and falsifies the audit.

Do not put a TCP forward in front of Whiskers. If you must:

- Point it at a non-loopback address that is not in `ForwardedHeaders__TrustedNetworks`: the host's LAN or tailnet IP,
  or the container's own address. Do not point it at `localhost` or `127.0.0.1`, because loopback is trusted by
  default.
- Make sure the address the forward comes from is not in `ForwardedHeaders__TrustedNetworks`.

The sidecar layout above suits HTTP mode only: in a sidecar, a TCP forward to `localhost` arrives from a trusted peer.

**Never combine `AUTH_DISABLED=true` with a TCP forward or with Funnel.**

- **TCP forward:** every visitor arrives from a private address and names its own `Host`, so every visitor becomes
  Admin, whatever their real address. With Funnel, that is anyone on the internet.
- **Funnel:** the instance is on the internet, and a single change of mode makes it Admin for everyone.

## Deployment profiles (full vs. hardened)

- **Full** (`docker-compose.yml`, default): privileged + host PID namespace so Whiskers can manage its **own** host
  (firewall/Nginx/systemd/`execute_command` via `nsenter`) and run an in-container VPN.
- **Hardened** (`docker-compose.hardened.yml`): for monitoring **remote** hosts only — non-root, no `privileged`,
  dropped capabilities, read-only rootfs, and Docker access through a verb-restricted socket-proxy. Trades away local
  host-shell management for a much smaller attack surface.

Full matrix and trade-offs: [container-hardening.md](container-hardening.md).

## Kubernetes (Helm)

Run Whiskers **on** a cluster as the control plane for your remote Docker fleet (and Kubernetes clusters — pods appear
next to containers on the dashboard):

```bash
( umask 077; openssl rand -hex 32 | tr -d '\n' > vault.key )          # a private key file, never an argument
kubectl create secret generic whiskers-vault --from-file=vaultKey=vault.key
# copy vault.key off the cluster (password manager / offline), then: shred -u vault.key
helm install whiskers oci://ghcr.io/lupusmalusdeviant/charts/whiskers --version X.Y.Z \
  --set vault.existingSecret=whiskers-vault
kubectl port-forward svc/whiskers 8080:8080   # → http://localhost:8080 (setup wizard)
```

Single-replica by design (Blazor Server + stateful loops), non-root, read-only rootfs, PVC-backed data. No VPN daemon
in the app container (`VPN_PROVIDER=none`); mesh access goes through an optional pinned Tailscale sidecar whose node
identity lives on its own PVC. All values, ingress/WebSocket notes and the Postgres variant:
[deploy/helm/whiskers/README.md](../deploy/helm/whiskers/README.md). To *manage* a cluster from Whiskers instead, see
[deploy/k8s/README.md](../deploy/k8s/README.md).

## From source (development)

```bash
git clone https://github.com/LupusMalusDeviant/Whiskers.git
cd Whiskers
docker compose up -d          # builds the image locally
```

Copy [.env.example](../.env.example) to `.env` to override any defaults. `.env` is gitignored, so secrets never land in
the repository.
