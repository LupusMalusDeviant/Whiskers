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
6. **Verify.** The agent re-reads the container's health: back to healthy. (In the reproduced
   screenshot demo the healthcheck is rigged to keep failing, so `payments-api` stays unhealthy.)
7. **Audit.** **Agent History** shows every step with its verdict; **Audit Log** shows the attempt
   that was held (failed) and the approved restart (succeeded), each with the agent as actor.

Then show the guardrail *biting*: the agent attempts a destructive `execute_command`
(`docker system prune -af --volumes`) on a host. It never runs — the guardrail denies it,
and the denial is recorded in both **Agent History** (verdict `Deny`) and **Audit Log** (a failed
`host.exec`).

That is the whole pitch in one loop: the agent is useful, but it can only ever do what policy allows,
a human approves the sensitive step, and everything is on the record.

---

## Reproducing the demo

The story is played against a fresh instance and an isolated demo Docker daemon that runs only the
containers `web`, `cache`, `postgres`, `worker` and a deliberately unhealthy `payments-api`. Nothing
is seeded: the agent is an MCP key named `Demo agent`, and every row in Approvals, Agent History and
Audit Log comes from a real call that went through the guardrails. The exact steps are in
[screenshots.md § Reproducing](screenshots.md#reproducing).
