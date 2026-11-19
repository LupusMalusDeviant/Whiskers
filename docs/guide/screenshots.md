# Screenshots

Captured against an isolated demo Docker daemon; the history comes from real MCP calls of a demo agent. How each
image is reproduced: [product/screenshots.md](../product/screenshots.md); the scripted walkthrough:
[product/demo-script.md](../product/demo-script.md). The in-app handbook (*Help*) has screenshots for every area.

**Guardrails.** The policy checked at the tool boundary: a read-only kill switch and the exceptions that stay allowed
while it is on, the highest level allowed without confirmation, an optional second approver, protected resources, and
forbidden argument patterns.

![Guardrails: a read-only kill switch with its fixed exceptions, maximum autonomous level, writes requiring confirmation, a second-approver switch, protected resources and forbidden argument patterns](../product/screenshots/governance-guardrails.png)

**Approvals.** A write the active preset holds back: the agent waits until a person approves or rejects it. Risk,
target and the (redacted) parameters are shown before the decision.

![Approvals: a pending update_container request from the Demo agent key, held by the Safe operations preset, with Approve and Reject; below it an approved restart_container](../product/screenshots/governance-approvals.png)

**Agent History.** Every agent/MCP tool call, with actor, privilege level (read / write / admin), the guardrail verdict
(Allow / Confirm / Deny), duration and outcome.

![Agent History: a table of tool calls showing actor, level, verdict and status, including an admin-level call denied by policy](../product/screenshots/governance-agent-history.png)

**Audit Log.** Every change with actor, target and outcome. Calls the guardrails held or denied appear as failed
attempts: they did not run. The approved restart appears as succeeded.

![Audit Log: entries by the Demo agent and the admin with action, target and status, including failed held and denied calls and a succeeded container restart](../product/screenshots/governance-audit-log.png)

**AI Operations onboarding.** Connect a client, create a read-only key, and activate a starter guardrail preset
(Observe only / Safe operations / Approval required).

![Secure AI Operations onboarding: connect a client, create a read-only key, choose a starter guardrail preset](../product/screenshots/governance-ai-operations.png)

**Dashboard.** Status across your hosts, here the local Docker host with its summary.

![Container overview dashboard: total/running/stopped/unhealthy counts and the expanded summary of the local Docker host](../product/screenshots/dashboard-overview.png)

**Topology.** The containers of a host as nodes; edges appear for shared networks and links.

![Topology: the five demo containers of the local host as nodes](../product/screenshots/topology.png)
