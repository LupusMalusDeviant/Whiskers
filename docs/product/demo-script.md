# Demo script — the hero workflow

A short, repeatable walkthrough that shows the one thing Whiskers is about: **humans and AI agents
operate infrastructure without unrestricted SSH/root — every action permissioned, policy-checked,
auditable.** Use it for a live demo, a screen recording, or to reproduce the screenshots in
[screenshots.md](screenshots.md).

The narrative follows the governance loop: **Observe → Propose → Check policy → Request approval →
Execute → Verify → Audit.**

---

## The story (≈3 minutes)

1. **Observe.** On the **Overview** dashboard, one workload is unhealthy (`payments-api`). The agent —
   or the operator — notices it the same way: from the live status, not from an SSH session.
2. **Propose.** The agent reads the container's logs (a `read`-level tool, allowed automatically) and
   concludes the liveness probe is timing out. It proposes a fix: restart the container.
3. **Check policy.** `restart_container` is a `write`-level tool. The **active guardrail preset**
   (see **Guardrails**) allows reads automatically but requires human confirmation for writes, so the
   call is held — the agent cannot execute it on its own.
4. **Request approval.** A confirmation request appears (with the exact tool, target and parameters).
   The operator approves it.
5. **Execute — once.** The *same* call instance runs exactly once. The correlation id ties the
   proposal, the approval and the execution together.
6. **Verify.** The agent re-reads the container's health: back to healthy.
7. **Audit.** **Agent History** shows every step with its verdict; **Audit Log** shows the
   `approval.granted` and the resulting `container.restart`.

Then show the guardrail *biting*: the agent attempts a destructive `execute_command`
(`docker system prune -af --volumes`) on a production host. It never runs — the guardrail denies it,
and the denial is recorded in both **Agent History** (verdict `Deny`) and **Audit Log**
(`guardrail.denied`).

That is the whole pitch in one loop: the agent is useful, but it can only ever do what policy allows,
a human approves the sensitive step, and everything is on the record.

---

## Reproducing the demo dataset

The screenshots are captured against an **anonymized demo dataset** — no real hosts, no real
container names. The dataset is seeded directly into a throwaway metrics database so the governance
history is deterministic and privacy-safe.

**Data that is seeded** (into `metrics.db`, a fresh `WHISKERS_DATA_DIR`):

- `McpToolCalls` — the tool-call history for the story above: `list_containers` / `get_container_logs`
  (read / Allow), `restart_container` (write / Confirm → approved), `get_container_metrics`
  (read / Allow), a denied `execute_command` (admin / Deny), plus human-operator calls
  (`get_container_cves`, `update_container`, `backup_database`). Actors are `Whiskers Agent` and
  `admin@acme.example`; every write call carries a correlation id.
- `AuditLog` — the matching trail: `approval.granted`, `container.restart`, `guardrail.denied`.

**Live surfaces** (Dashboard) are shown against a local Docker host running a handful of throwaway
containers with generic names (`web`, `cache`, `postgres`, `worker`, and a deliberately unhealthy
`payments-api`). Only the connected-host summary (OS / CPU / memory / usage charts) is captured — never
a real fleet.

**Boot for the demo:**

```bash
# a throwaway data dir with the seeded metrics.db + a single local Docker server
WHISKERS_DATA_DIR=/path/to/demo-data \
Auth__Disabled=true \
ASPNETCORE_ENVIRONMENT=Development \
Docker__SocketPath="npipe://./pipe/docker_engine"   # unix:///var/run/docker.sock on Linux
dotnet run --project src/Whiskers/Whiskers.csproj --urls http://127.0.0.1:5190
```

`Auth__Disabled=true` is for local capture only — never in a deployment. Set the UI language to
English (default) via the language switcher, then walk the routes in [screenshots.md](screenshots.md).
