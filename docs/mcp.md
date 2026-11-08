# MCP server

Whiskers serves MCP over Streamable HTTP at `/mcp`. Client setup: [installation.md → MCP clients](installation.md#mcp-clients).

```json
{
  "mcpServers": {
    "whiskers": {
      "type": "http",
      "url": "https://whiskers.example.com/mcp",
      "headers": { "Authorization": "Bearer <WHISKERS_MCP_API_KEY>" }
    }
  }
}
```

Behind a reverse proxy on a subpath (`PATH_BASE=/whiskers`) the URL becomes `https://example.com/whiskers/mcp`; for a
local instance it is `http://localhost:5100/mcp`.

## API keys and scopes

- **Keys are created in the web UI** under *Settings → MCP* by an Admin. Each key carries exactly one scope —
  **Read**, **Write** or **Admin** — or, instead of a scope, an explicit list of allowed tools. Keys can be disabled or
  deleted there at any time; a disabled key is rejected on the next request.
- **Every tool declares its minimum scope in code** and the check runs on every call. A scoped key never reaches a tool
  above its scope; a tool without a configured level is denied (fail-closed); an Admin can switch individual tools off
  for every key.
- **Keys are stored only as hashes.** A key is 256 bits from a cryptographic RNG; Whiskers shows it **once**, right
  after creation, and persists only its SHA-256 hash plus the last four characters for recognition. A presented key is
  hashed and compared in constant time. Neither the key nor any part of a rejected token is written to the log.
  Someone who reads the data directory or a backup gets hashes, which do not authenticate. Requests without a valid
  key are rejected before any tool runs.
- A fresh install has no key. Keys from older versions are converted to hashes on the first start and keep working.
  Grant Write/Admin only to keys that need it.

> **Note for older index entries (e.g. MCP directories that crawled the ServerWatch-era README):** Whiskers was called
> *ServerWatch* before the rebrand in v0.12. The old flat, unscoped key file `/app/data/api-keys.json` is **gone**: its
> last compatibility fallback was removed in v0.15.0, no code path reads it, and a leftover copy is deleted on start.
> MCP access works only with a scoped key created in the web UI, and those keys are stored as SHA-256 hashes, never in
> plaintext.

## Tools

- **Containers**: list/inspect/logs/metrics/env, start/stop/restart/update
- **Server & host**: info, logs, metrics, health summary, `execute_command` (Admin)
- **Deployment**: `deploy_app`, `deploy_compose`
- **Infrastructure**: firewall, Nginx, systemd, SSL
- **Databases**: detect, list, schema, query, backup
- **Networks**: list/create/remove, connect/disconnect containers
- **Logs & alerts**: search, list/create log alerts
- **Scheduler**: list/create/delete/run scheduled tasks
- **CVEs & updates**: server/container CVE summaries, update status
- **Cloud (out-of-band)**: Hetzner & Hostinger power/snapshot/metrics
- **Agent**: `instruct_agent` (delegate a natural-language task to the in-process agent)

The complete list with permission levels is generated from the code: [mcp-tool-catalog.md](mcp-tool-catalog.md). The
web UI shows it under *Settings → MCP*.

## Prompts

User-invokable workflows. They only return instructions; every tool call they lead to is still scope- and
guardrail-checked.

| Prompt | What it does |
|---|---|
| `triage_unhealthy_containers` | Read-only diagnosis of unhealthy/stopped/restarting containers, ranked fixes |
| `security_review_server` | Read-only review of one server: CVEs, firewall, TLS expiry, unexpected services |
| `plan_safe_update` | Risk, blast radius and CVE check for an image update, plan with rollback — stops before changing anything |
| `investigate_host_load` | Explains host load, including load no container accounts for |

## Resources

Read-only context about the MCP surface itself, no infrastructure data:

| URI | Content |
|---|---|
| `whiskers://tools/catalog` | Every served tool with its required level and whether it is enabled |
| `whiskers://me/permissions` | The caller's own scope and the tools it may call |

## Policy layer

Scope check, guardrail engine and approvals per caller: [governance.md → How the policy layer applies](governance.md#how-the-policy-layer-applies).
