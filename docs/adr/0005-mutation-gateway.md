# ADR-0005: Mutation Gateway — one enforcement point for every infrastructure mutation

- **Status:** Proposed (2026-10-26), awaiting maintainer approval before WP2 starts
- **Date:** 2026-10-26
- **Author:** LupusMalusDeviant
- **Consulted:** —
- **Context documents:** [`docs/v1/PRODUCT_CONTRACT.md`](../v1/PRODUCT_CONTRACT.md) (decisions PC-D1…D8),
  [`docs/v1/SECURITY_INVARIANTS.md`](../v1/SECURITY_INVARIANTS.md) (INV-xx), [`docs/v1/TECHNICAL_AUDIT.md`](../v1/TECHNICAL_AUDIT.md) §7.1

## Context and problem

The product contract (PC-D1) says: *no infrastructure mutation that Whiskers performs bypasses authentication,
authorization, policy, a required approval, or audit.* The audit found 90 mutation paths (41 MCP tools, 4 agent
paths, 8 background paths, 3 terminals, 34 UI handler groups). Today the chain is assembled **at each entry point**:

- MCP tools call `McpPermissionCheck.CheckAccess`, resolve their target, call the service and write the AuditLog
  themselves (for example `ContainerTools.StartContainer`). Some forget the audit (B-04).
- Policy runs in two places only: `McpGuardrailFilter` → `DirectMcpCallGuard` for direct MCP calls, and
  `AgentSession` / `AgentToolInvoker` for the agent. Both live in the Agent module (TA-27).
- UI handlers check a role and call the service. Scheduler, AutoUpdate, AutoPatch, webhooks and terminals call
  executors directly with no policy and no AuditLog (TA-04, TA-05).
- Levels live in two models (`DefaultToolLevels` + `[McpToolLevel]` for MCP, `AppRole` checks in pages) that
  disagree (TA-28).

Every new entry point must re-implement the chain, and most did not. The invariants INV-13, 15, 20, 21, 22, 38, 39,
43, 45 and 48 cannot hold while enforcement is spread over entry points.

**Core question:** Where and how is the chain *Actor → AuthN → AuthZ → Policy → Approval → Execution → Verification
→ Audit* enforced so that it holds for every path, including paths added later?

## Requirements

### Functional

- One place decides and records every mutation: authorization against one capability table, policy decision,
  approval binding, execution under a deadline, verification, audit (INV-13, 20, 22, 38, 43, 45).
- Works for all surfaces: MCP, agent, UI, automation, webhook, terminal open/close (PC-D1, PC-D2).
- Interactive surfaces can wait for an approval; non-interactive surfaces get approve-then-retry bound to the same
  operation (INV-30, 39).
- The read-only kill switch and protected resources apply on every surface (INV-21, 24, 59).
- The audit intent is written before execution; if that fails, nothing executes (PC-D5, INV-48).
- Unattended automation runs under a recorded owner whose authority is re-checked per run (PC-D6, INV-04).

### Non-functional

- **Incremental adoption.** The gateway is introduced surface by surface (WP2 order: MCP + agent, UI, scheduler,
  AutoUpdate/AutoPatch, webhooks, terminals). Each step ships on its own and keeps the app releasable.
- **Bypass is detectable.** A path that calls a mutating service without the gateway fails a test, and later fails at
  runtime.
- **Protected layers stay untouched.** No change to the authentication middleware order, the OAuth whitelist, the mTLS
  layer or the socket-proxy verb whitelist. The gateway sits behind authentication; it consumes the principal.
- **Single replica, in process.** No new service, no network hop. Overhead per mutation: a few DB writes, which is
  negligible next to a Docker or SSH call.
- **Interface-first**, DI-registered, testable without Docker (`[Collection("WebAppBoot")]` wire tests for the pipeline).

## Options

### Option A — In-process Mutation Gateway with a capability catalog (recommended)

A core service `IMutationGateway` owns the chain. Entry points describe *what* they want; the gateway decides,
executes a supplied delegate, verifies and records.

```csharp
public interface IMutationGateway
{
    Task<MutationOutcome<T>> ExecuteAsync<T>(
        MutationRequest request,                      // capability, principal, surface, target ref, args, operation id
        Func<MutationContext, CancellationToken, Task<T>> execute,
        CancellationToken ct = default);
}
```

- **Capability catalog** (`ICapabilityCatalog`): one entry per mutating action, for example `container.start`,
  `host.exec`, `compose.deploy`, `db.query`, `terminal.host.open`. Each entry has the required level, the target
  kind, a risk class, a verifier, and whether humans need an approval by default (no, per PC-D1). MCP tools reference
  their capability (`[Capability("container.start")]`). UI handlers, jobs and webhooks pass the capability id. The
  catalog replaces `DefaultToolLevels` as the single source of levels (INV-13); the Enabled switch and stored
  stricter levels are applied there for every principal kind (INV-15).
- **Pipeline inside the gateway:**
  1. resolve and canonicalize the target (INV-57);
  2. authorize the principal against the catalog;
  3. evaluate policy and persist a `PolicyDecision` record (INV-22);
  4. on `Confirm`: create or consume a persisted, hash-bound approval (WP3). Interactive callers await it,
     non-interactive callers get `PendingApproval` with the operation id;
  5. write the audit intent; on failure stop (PC-D5);
  6. execute the delegate under a deadline (INV-40);
  7. run the capability's verifier (INV-43);
  8. write the audit outcome (WP5 adds the hash chain).
- **Execution scope guard.** The gateway opens an ambient `MutationScope` (`AsyncLocal`) around the delegate. Mutating
  service methods call `MutationScope.Require("container.start")`. During migration a missing scope is logged and
  counted; once every surface is migrated it throws, which makes bypass fail closed at runtime (INV-38).
- **Architecture test.** A reflection test lists every constructor and tool-method dependency on a mutating service
  interface and fails if it is outside the gateway or a registered capability handler.
- **Policy engine moves to core** (PC-D8). `IAgentGuardrailEngine`, `GuardrailStore` and approvals stop depending on
  the Agent module; `McpGuardrailFilter`, `DirectMcpCallGuard` and the gate in `AgentToolInvoker` become thin
  adapters onto the gateway and are removed once MCP and agent are migrated.

*Pros:* one chain, one table, testable in isolation; the scope guard plus architecture test make bypass visible
without trusting reviewers; entry points shrink (tools lose their own permission and audit code).
*Cons:* touches every mutating entry point (90 paths) over several PRs; tool and handler signatures change; the scope
guard needs care in services that call other mutating services internally (for example update = stop + recreate:
the outer capability's scope covers the inner calls).

### Option B — Decorators on the mutating services

Wrap `IDockerService`, `IHostCommandExecutor`, `IFirewallService`, … in DI decorators that enforce policy and audit
at the service boundary, with the principal taken from an ambient context.

*Pros:* catches every caller automatically, including future ones; few call-site changes.
*Cons:* service methods are the wrong granularity: one user intent (update a container) is several service calls,
so approvals and audit bind to fragments instead of the intent. Read and write methods share interfaces. The
principal must travel implicitly to background jobs, which is fragile. Approvals cannot be awaited cleanly from inside
a service call. Verification has no natural home.

### Option C — Keep enforcement at the entry points, add shared helpers and tests

Extract a helper for "check, decide, audit" and require every entry point to call it; add tests per entry point.

*Pros:* smallest change; no new abstraction.
*Cons:* the same structure that produced the current gaps; correctness depends on every author remembering every
step; INV-38 cannot be enforced; no single place for decision records, idempotency or the hash chain.

### Option D — External policy engine (OPA / sidecar)

Delegate decisions to an out-of-process engine.

*Pros:* industry-standard policy language, separation of duties for policy authors.
*Cons:* a second runtime for a single-replica appliance, a network hop on every mutation, a new failure mode for a
fail-closed path, and it solves only the policy step, not execution, verification and audit. Can still be added
later behind the gateway's policy step.

## Decision

**Option A.** The Mutation Gateway becomes the only path to an infrastructure mutation. It is core (not a module),
owns the capability catalog, and enforces the full chain. The ambient scope guard from Option A takes the one real
advantage of Option B — catching unknown callers — without moving decisions to the wrong granularity.

### Rollout (WP2)

| Step | Scope | Exit criterion |
|---|---|---|
| 2.0 | Gateway skeleton, `MutationRequest`/`Outcome`, capability catalog generated from today's levels (no behaviour change), scope guard in **report** mode, architecture test listing current bypasses as an explicit baseline | Build + tests green; baseline file lists all 90 paths |
| 2.1 | Policy engine and approval store moved to core (PC-D8); MCP tools and agent through the gateway; `DirectMcpCallGuard` / invoker gate become adapters | MCP + agent rows of the path matrix have no gateway gap; INV-15, 26 ✅ |
| 2.2 | UI handlers (incl. PC-D3 `db.query` = Admin, PC-D2 container terminal = Admin) | U-rows migrated; upgrade notes |
| 2.3 | Scheduler, AutoUpdate, AutoPatch with owner principal (PC-D6) | S-01..S-03 migrated; INV-04 ✅ |
| 2.4 | Webhooks and git deploy | S-04, S-05 migrated |
| 2.5 | Terminal open/close (PC-D2) | T-rows migrated; INV-47 ✅ |
| 2.6 | Scope guard switched to **enforce**; baseline file empty and deleted | INV-38 ✅ |

Persisted approvals (WP3), decision records (WP4) and the audit hash chain (WP5) plug into steps 3–4 and 8 of the
pipeline. Until then the gateway uses today's stores behind the same interfaces, so later WPs change storage, not
call sites.

## Consequences

### Positive

- The contract holds by construction for migrated paths, and the remaining bypasses are listed in a baseline file
  that can only shrink.
- One capability table ends the UI/MCP level mismatch and makes the Settings switches effective everywhere.
- Every mutation gets an operation id, a decision record, a verification result and one audit event — the material
  a customer audit asks for.
- Tools and pages become shorter: authorization and audit code leaves the entry points.

### Negative

- A long PR series touching most of the codebase; merge conflicts with parallel work are likely during WP2.
- Fail-closed audit (PC-D5) means a broken database blocks mutations. This is intended; break-glass is host access.
- Behaviour changes need upgrade notes: container terminal and "Run now" of custom commands become Admin,
  `execute_query` becomes Admin, AI triggers become read-only by default (PC-D7).
- The scope guard adds an `AsyncLocal` read to mutating service methods; negligible, but it must be propagated
  correctly into background work started inside a mutation.

### Neutral / follow-ups

- The MCP approve-then-retry flow stays for non-interactive clients, but binds to the operation id and the persisted
  approval instead of an in-memory grant.
- `[McpToolLevel]` is replaced by `[Capability]`; `McpToolLevelTests` turns into a catalog consistency test.
- An external policy engine (Option D) remains possible behind the gateway's policy step if a customer requires it.

## Open points for the maintainer

1. Approve Option A and the rollout order above.
2. Confirm that WP2 may move the guardrail engine and approvals out of the Agent module (PC-D8 implies it; it changes
   module boundaries from RoadToSAP §3).
3. Confirm the naming (`IMutationGateway`, `ICapabilityCatalog`, capability ids `<area>.<verb>`).
