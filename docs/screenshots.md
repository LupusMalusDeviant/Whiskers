# Screenshots

Captured against an anonymized demo dataset. How each image is reproduced: [product/screenshots.md](product/screenshots.md);
the scripted walkthrough: [product/demo-script.md](product/demo-script.md). A live walkthrough of the hero workflow is
on [whiskers.app.lupusmalus.dev](https://whiskers.app.lupusmalus.dev), and the in-app handbook (*Help*) has screenshots
for every area.

**Guardrails.** The policy checked at the tool boundary: a read-only kill switch, the highest level allowed without
confirmation, protected resources, and forbidden argument patterns.

![Guardrails: a code-enforced policy with a read-only kill switch, maximum autonomous level, protected resources and forbidden argument patterns](product/screenshots/governance-guardrails.png)

**Agent History.** Every agent/MCP tool call, with actor, privilege level (read / write / admin), the guardrail verdict
(Allow / Confirm / Deny), duration and outcome.

![Agent History: a table of tool calls showing actor, level, verdict and status, including an admin-level call denied by policy](product/screenshots/governance-agent-history.png)

**Audit Log.** The approval and execution trail: who approved what, which action a guardrail blocked, and the
resulting change.

![Audit Log: approval-granted and guardrail-denied entries with actor, action and target](product/screenshots/governance-audit-log.png)

**AI Operations onboarding.** Connect a client, create a read-only key, and activate a starter guardrail preset
(Observe only / Safe operations / Approval required).

![Secure AI Operations onboarding: connect a client, create a read-only key, choose a starter guardrail preset](product/screenshots/governance-ai-operations.png)

**Dashboard.** Status across Docker hosts and Kubernetes workloads, with live host metrics.

![Container overview dashboard: total/running/stopped/unhealthy counts and a connected Docker host with live CPU and memory charts](product/screenshots/dashboard-overview.png)
