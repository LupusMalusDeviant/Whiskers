# Screenshots

Canonical product screenshots, mapped to the three pillars of the positioning
([POSITIONING.md](POSITIONING.md)). All images live in [`screenshots/`](screenshots/) and are embedded
in the top-level [README](../../README.md#screenshots) and on the project website.

Capture conventions: **1440×900**, device-scale 1, **English UI** (the product default), dark theme,
against the anonymized demo dataset from [demo-script.md](demo-script.md).

| File | Route | Pillar | Shows |
|---|---|---|---|
| `governance-guardrails.png` | `/guardrails` | Enforced governance | Code-enforced, non-bypassable policy at the tool boundary: read-only kill switch, max autonomous level, protected resources, forbidden argument patterns. |
| `governance-agent-history.png` | `/agent-history` | Controlled access | Every agent/MCP tool call — actor, level (read/write/admin), verdict (Allow/Confirm/Deny), duration, status — including an admin call denied by policy. |
| `governance-audit-log.png` | `/audit-log` | Complete accountability | The approval + execution trail: `approval.granted`, `guardrail.denied`, `container.restart`. |
| `governance-ai-operations.png` | `/ai-operations` | Governed setup | Connect a client, mint a least-privilege read-only key, activate a starter guardrail preset. |
| `dashboard-overview.png` | `/` | Reach | Container overview (total/running/stopped/unhealthy) + a connected Docker host with live CPU/memory charts. |

## Reproducing

1. Seed the demo dataset and boot the app — see [demo-script.md](demo-script.md).
2. Set the UI language to English via the top-right switcher.
3. For the static pages (`/guardrails`, `/agent-history`, `/audit-log`, `/ai-operations`), a
   server-rendered capture at 1440×900 is sufficient — Blazor Server prerenders the content.
4. For the **dashboard**, a live circuit is needed for the host to report as connected, so drive a
   real browser (the shipped headless Chrome via CDP works): navigate to `/`, wait for the local host
   to show connected, expand its summary panel, and capture the top region. The per-container list is
   **not** captured — it would show real container names on the capture host.

## Anonymization

- Governance history is fully synthetic (`Whiskers Agent`, `admin@acme.example`, `prod-eu-*`,
  `staging-1`, `payments-api`, `orders`, `web`). No real hosts, users or secrets.
- The dashboard shows only the connected-host **summary** (OS / CPU / memory / charts). Never capture
  the container list on a machine that also runs unrelated containers.
- Re-check every image for real hostnames, IPs, emails or tokens before committing.

## Not yet captured

- **Live approval card** (a *pending* confirmation) — needs a real agent run against a configured LLM
  provider. The approval flow is currently evidenced through Agent History + Audit Log instead.
- **Light-theme variants** and a short **demo video** of the full hero loop.
- A clean **per-container list** view (running-only), which needs a capture host with only demo
  containers present.
