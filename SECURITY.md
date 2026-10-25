# Security Policy

Whiskers is infrastructure-management software: it reaches into Docker hosts, runs host
commands, manages firewalls/Nginx/systemd, and exposes an MCP server to AI agents. Security is a
first-class concern of the design, please treat findings accordingly.

## Reporting a vulnerability

**Please report vulnerabilities privately, do not open a public issue.**

Use GitHub's private reporting: **Security > Advisories > "Report a vulnerability"** on this
repository. Include:

- a description and impact assessment,
- steps to reproduce (a minimal PoC if possible),
- affected version / commit,
- any suggested remediation.

### What you can expect

Whiskers has a single maintainer, so these are commitments sized to that — not a staffed SOC:

| Step | Target |
|---|---|
| Acknowledgement of your report | within **72 hours** |
| Triage: confirmed or not, severity (CVSS) | within **7 days** |
| Fix released for **critical / high** | within **14 days** of confirmation |
| Fix released for **medium / low** | within **30 days**, or with the next release |
| Public advisory (GitHub Security Advisory, credit if you want it) | with the fixed release |

If a target slips, you will hear so — with a reason and a new date — rather than silence. Please allow
until the fixed release before any public disclosure (coordinated disclosure); after 90 days you are free
to disclose regardless.

## Supported versions

Whiskers is currently **beta** (`0.14.x`). Only the latest release / `main` receives security
fixes. There is no long-term-support branch yet.

| Version | Supported |
|---|---|
| `0.14.x` (beta) | ✅ latest only |
| `< 0.14` | ❌ |

## Security model (summary)

- **No standing SSH key** for managed hosts in steady state, management runs over a private
  WireGuard mesh with mutual-TLS Docker access. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Management ports are mesh-bound**: nothing management-related is exposed to the public internet
  by design.
- **MCP access is gated per API key** as Read / Write / Admin.
- **The acting agent is bounded by code-enforced guardrails** evaluated at the tool boundary (not in
  the prompt) and can never exceed the rights of whoever/whatever triggered it.
- **Secrets** (`.env`, API keys, tokens, certificates, the encrypted vault, the SQLite DB) live under
  `/app/data` / `.env`, both gitignored and volume-mounted, never baked into the image or committed.

## Hardening checklist for operators

- Set `AUTH_DISABLED=false` and put a real IdP (Google OAuth or OIDC) in front, or keep the app on a
  trusted private network only.
- Keep the email whitelist tight; review roles under *Settings > Authentication*.
- Issue MCP API keys with the **least** permission level needed.
- Keep the agent's guardrail presets restrictive; only widen autonomy deliberately.
- Set a strong `VAULT_KEY` and protect the `/app/data` volume (it holds plaintext provider keys by
  design choice and the data-protection keys).
- Keep dependencies current, Dependabot config ships in `.github/dependabot.yml`.
- **Run the least-privileged container profile that fits.** The default `docker-compose.yml` is
  privileged because it can manage its own host (nsenter host-shell). If Whiskers only monitors
  *remote* hosts, deploy `docker-compose.hardened.yml` instead (non-root, no `privileged`, dropped
  capabilities, read-only rootfs, Docker via a verb-restricted socket-proxy). See
  [docs/container-hardening.md](docs/container-hardening.md).
