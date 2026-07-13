<!--
  Canonical product-positioning baseline (WP-01).
  This file is the single source of truth for the Whiskers one-liner, pitch, target
  audience, product pillars, hero workflow, approved/avoided wording and glossary.
  README, website and in-app help should draw their product language from here.
  Written in English because these claims are public; a German rendering of the
  one-liner is included so the bilingual website can stay semantically in sync.
-->

# Whiskers — Product Positioning

**Status:** canonical product baseline · **Audience:** maintainers, docs, website, marketing
**Scope:** what Whiskers *is* and how we talk about it. Not a feature backlog.

---

## One-liner

> **Controlled infrastructure operations for humans and AI agents.**

Longer canonical form (use where a single sentence is allowed):

> **Whiskers is the self-hosted control plane that lets humans and AI agents operate
> infrastructure without handing them unrestricted SSH or root access — every action is
> permissioned, policy-checked and auditable.**

German rendering (for the bilingual website; keep semantically in sync):

> **Whiskers ist die selbst gehostete Control Plane, mit der Menschen und KI-Agenten
> Infrastruktur bedienen können, ohne unkontrollierten SSH- oder Root-Zugriff zu erhalten.
> Jede Aktion ist berechtigt, durch Richtlinien geprüft und nachvollziehbar protokolliert.**

---

## Elevator pitch

AI agents can now operate real infrastructure — but the usual way to let them is to hand over
an SSH key or a shell, which grants far more power than any single task needs. Whiskers puts a
**self-hosted control plane** between the operator (human **or** AI) and the infrastructure.
Instead of unrestricted shell access, it exposes **explicit tools** that are gated by
authentication and per-key permissions, checked against **code-enforced guardrails**, and — for
consequential actions — held for **human approval**. Every step is recorded, with secrets
redacted, so you can always answer *who did what, to which target, with which parameters, and
what happened*.

The same control plane reaches Docker hosts, Kubernetes workloads, logs, deployments, services
and CVE context — but the reach is *evidence of coverage*, not the headline. The headline is
**governed, accountable operations**.

---

## Target audience

Primary (kept deliberately narrow):

> **Small software teams, agencies, managed-service providers and technically-led SMBs that run
> several Linux / container hosts and want to use AI-assisted operations without giving agents
> unrestricted shell or root access.**

Explicitly **not** the primary paying audience (still welcome as community users and testers):

- home-lab users as the main revenue target
- large enterprise platform teams with an existing internal control plane
- pure Kubernetes-platform teams
- users who only want a simple Docker-Compose deploy service
- general consumers

The audience is intentionally tighter than *"everyone running Docker"*.

---

## Problem statement

Letting an AI agent (or a junior operator) act on infrastructure usually means giving it an SSH
key or a shell. That credential is *god mode*: it can do anything on the host, it rarely expires,
and if the controller is compromised, every server it holds a key for falls with it. There is no
built-in notion of *"this actor may only restart this one container, and only with approval,"* and
no reliable, tamper-evident record of what an autonomous actor actually did.

Prompt-level rules ("please don't delete anything") are not a control: the model can ignore them,
be jailbroken, or simply be wrong.

---

## Value proposition

Whiskers replaces *"hand over a shell"* with *"expose explicit, governed capabilities"*:

- **Controlled access** — humans and agents get only the capabilities you grant, as tools, scoped
  by an API-key level (Read / Write / Admin) or an explicit tool list.
- **Enforced governance** — guardrails are evaluated **server-side at the tool boundary**, not in
  the prompt; sensitive actions can require a human approval before they run.
- **Complete accountability** — Agent History and the Audit Log record actor, action, target,
  parameters (secrets redacted), decision and result.

Because the control plane is one layer, the same governance applies whether the action came from a
web user, the in-app agent, or an external MCP client such as Claude Code.

---

## The three product pillars

| Pillar | What it means | Backed by |
|---|---|---|
| **1. Controlled access** | Humans and agents receive only explicitly allowed capabilities. | Authentication + roles; MCP API keys with Read/Write/Admin levels or explicit tool lists. |
| **2. Enforced governance** | Policy is enforced in code at the tool boundary; consequential actions can require human approval. | Guardrail presets with per-tool **Allow / Confirm / Block**; human-in-the-loop approvals. |
| **3. Complete accountability** | Every action leaves tamper-evident evidence, with secrets redacted. | Agent History (every tool call) + Audit Log (user actions). |

Docker, Kubernetes, logs, deployments, CVEs and other modules demonstrate the **reach** of the
control plane. They are evidence of coverage, not the primary message.

---

## Message hierarchy (order is binding)

1. **Problem** — AI agents can operate infrastructure, but direct SSH/root access gives them far
   more power than the task requires.
2. **Solution** — Whiskers exposes explicit infrastructure capabilities through a self-hosted
   control plane instead of handing out unrestricted shell access.
3. **Governance** — permissions, code-enforced guardrails, approvals and audit trails govern every
   action.
4. **Reach** — the same control plane manages Docker hosts, Kubernetes workloads, logs,
   deployments, CVEs, services and related operations.
5. **Architecture** — optional mesh + mTLS operation removes standing SSH credentials after
   onboarding.

Feature reach must never lead ahead of the problem and the governance promise.

---

## Hero workflow

The product's most important end-to-end flow:

> **Observe → Propose → Check policy → Request approval → Execute → Verify → Audit**

Concrete demo scenario:

1. A container / workload is unhealthy.
2. The agent analyses status and logs using **read** permissions.
3. The agent proposes a restart.
4. The active guardrail rates the restart tool as **Confirm**.
5. Whiskers creates an approval request (actor, tool, target server, target workload, redacted
   parameters, rationale, expiry).
6. The user approves it.
7. Whiskers executes **only** the approved action.
8. Whiskers verifies the workload status afterwards.
9. Agent History and the Audit Log show the full chain.
10. The UI deep-links between result, approval and history.

This workflow is the reference for the website demo, AI-operations onboarding, screenshots, and the
acceptance test for governance features.

---

## Approved / avoided wording

**Prefer:**

- self-hosted control plane
- controlled infrastructure operations
- humans and AI agents
- code-enforced guardrails
- human-in-the-loop approvals
- permissioned
- auditable
- SSH-key-free steady-state operation
- mesh + mTLS
- infrastructure capabilities / tools

**Avoid, or use only as secondary support:**

- "Docker dashboard" / "AI chat for Docker" / "all-in-one server tool" (undersells the governance core)
- "replaces SSH" — say **SSH-key-free steady-state operation** (a one-time bootstrap SSH is used; see below)
- "zero trust" without a precise definition
- "unhackable", "enterprise-grade" without evidence
- "autonomous infrastructure" without the governance context
- "full Kubernetes management" — the Kubernetes scope is intentionally limited (pods, logs, honest scale/rollout)

**No unproven superlatives.** Every strong claim must be backed by something a reader can verify in
the repo or docs.

### Precise SSH statement (use verbatim where accuracy matters)

- A **one-time bootstrap SSH** connection during onboarding is allowed and expected.
- After onboarding, **steady-state operation is designed to run without a standing private SSH
  key** — management moves to mesh + mTLS (see [../ARCHITECTURE.md](../ARCHITECTURE.md)).
- We do **not** claim SSH is never used, and we do **not** claim Whiskers "replaces SSH".

---

## Glossary

Use these terms precisely and do not treat them as synonyms.

| Term | Meaning |
|---|---|
| **Control plane** | The Whiskers layer between operators (human/AI) and infrastructure. It exposes governed tools, not raw shells. |
| **Capability / tool** | A single explicit operation Whiskers exposes (e.g. "restart container"). Actors call tools, not arbitrary shells. |
| **Permissions / API-key level** | Per-MCP-key access: **Read**, **Write** or **Admin**, or an explicit allowed-tool list. Defines *what an actor may attempt*. |
| **Guardrails** | A code-enforced policy evaluated at the tool boundary. Each tool is **Allow**, **Confirm** or **Block** in the active preset. (Engine verdict values: `Allow`, `Confirm`, `Deny`.) Not a prompt instruction. |
| **Approval (human-in-the-loop)** | The pause a `Confirm` tool triggers: a human authorises or rejects the exact action + parameters before it runs. |
| **Agent History** | The record of every agent tool call: tool, actor/key, redacted parameters, decision, result, duration, target. |
| **Audit Log** | The record of user (human) actions in the app. Complements Agent History; the two are not the same thing. |
| **MCP** | Model Context Protocol — the authenticated *interface* through which external AI clients reach Whiskers tools. An interface, not the whole product. |
| **AI Advisor vs Acting Agent** | *Advisor* explains and proposes but runs nothing; *Acting Agent* may call tools — always bounded by guardrails and approvals. |
| **Workload provider** | The seam that lets the same control plane target **Docker** hosts or **Kubernetes** clusters. Docker/K8s are targets, not the product identity. |
| **Mesh + mTLS** | Private WireGuard mesh (Tailscale/NetBird) with mutual-TLS Docker access — the SSH-key-free steady-state transport. |
| **Vault / redaction** | Secrets live in the encrypted vault; sensitive values are redacted before logging and before persistence. |
| **Bootstrap SSH** | The one-time SSH connection used during onboarding to move a host onto the mesh; not a standing credential. |

---

## Related

- [README.md](README.md) — index of product-strategy documents.
- [../ARCHITECTURE.md](../ARCHITECTURE.md) — the SSH-key-free design, mesh + mTLS, the three planes.
- [../../SECURITY.md](../../SECURITY.md) — security model and hardening.
