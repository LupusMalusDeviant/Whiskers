# Security Policy

ServerWatch is infrastructure-management software: it reaches into Docker hosts, runs host
commands, manages firewalls/Nginx/systemd, and exposes an MCP server to AI agents. Security is a
first-class concern of the design — please treat findings accordingly.

## Reporting a vulnerability

**Please report vulnerabilities privately — do not open a public issue.**

Use GitHub's private reporting: **Security → Advisories → "Report a vulnerability"** on this
repository. Include:

- a description and impact assessment,
- steps to reproduce (a minimal PoC if possible),
- affected version / commit,
- any suggested remediation.

You will get an acknowledgement as soon as possible. Please allow reasonable time for a fix before
any public disclosure (coordinated disclosure).

## Supported versions

ServerWatch is currently **beta** (`0.9.0-beta`). Only the latest release / `main` receives security
fixes. There is no long-term-support branch yet.

| Version | Supported |
|---|---|
| `0.9.x` (beta) | ✅ latest only |
| `< 0.9` | ❌ |

## Security model (summary)

- **No standing SSH key** for managed hosts in steady state — management runs over a private
  WireGuard mesh with mutual-TLS Docker access. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Management ports are mesh-bound** — nothing management-related is exposed to the public internet
  by design.
- **MCP access is gated per API key** as Read / Write / Admin.
- **The acting agent is bounded by code-enforced guardrails** evaluated at the tool boundary (not in
  the prompt) and can never exceed the rights of whoever/whatever triggered it.
- **Secrets** (`.env`, API keys, tokens, certificates, the encrypted vault, the SQLite DB) live under
  `/app/data` / `.env` — both gitignored and volume-mounted, never baked into the image or committed.

## Hardening checklist for operators

- Set `AUTH_DISABLED=false` and put a real IdP (Google OAuth or OIDC) in front, or keep the app on a
  trusted private network only.
- Keep the email whitelist tight; review roles under *Settings → Authentication*.
- Issue MCP API keys with the **least** permission level needed.
- Keep the agent's guardrail presets restrictive; only widen autonomy deliberately.
- Set a strong `VAULT_KEY` and protect the `/app/data` volume (it holds plaintext provider keys by
  design choice and the data-protection keys).
- Keep dependencies current — Dependabot config ships in `.github/dependabot.yml`.
