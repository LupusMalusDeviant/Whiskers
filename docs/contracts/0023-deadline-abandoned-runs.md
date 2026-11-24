# Contract 0023 — Runs past their deadline, and the token ratchet (INV-40)

Status: **Draft** · 24 November 2026, revised the same day after review round 1. It waits for the maintainer's
acceptance and implements decision A2 of the 1.1.0 plan (24 November 2026):
- the deadline stays cooperative, and there is no hard abort;
- a run that outlives its deadline is marked `abandoned` and audited;
- a test forbids new places that discard the token, and the existing ones are fixed in batches.

The open decisions D1–D7 are at the end, each with a recommendation.

## Purpose

INV-40 says that every execution has a deadline that cancels its token. Contract 0017 part J made the wording
precise: the deadline is cooperative, and an action that ignores the token runs to its end. The 1.0.0 review counted
122 runner lambdas that discard the token (`(ResolvedContainer c, CancellationToken _) => Service.DoAsync(c.Id)`).

For those actions the deadline has no effect:
- the caller waits until the action ends, however long that takes;
- when the action finally returns, the run is recorded as `Executed`, as if it had kept its deadline;
- the UI page, the MCP client or the automation hears nothing about the deadline.

This contract does four things:
- it bounds how long the caller waits;
- it makes a run past its deadline visible;
- it keeps everything that protects the target held until the run really ends;
- it stops the number of discarded tokens from growing. How far that number comes down for 1.1.0 is D4.

## Current state (verified at `7d5bbe5e`)

**The gateway** (`MutationGateway.ExecuteAsync`) does the following:
- It links the caller's token with `capability.Deadline` (`CancelAfter`) and awaits `execute(context, deadline.Token)`
  without any bound.
  - An `OperationCanceledException` raised by the deadline gives `Failed`, with "exceeded its deadline … its effect is
    unknown".
  - An action that does not observe the token is awaited to its end, then verified and recorded as usual.
- It owns only the following across `execute`:
  - the **maintenance admission** (`_maintenance.TryEnter`, `using var admittedHandle`). A restore waits for it
    before it takes its safety backup. A nested gateway call inside the action skips the admission and relies on the
    outer one.
  - the **operation journal's claim**, per operation id. A retry under a new id is not stopped by it.
  - the **`MutationScope`** (AsyncLocal) and the deadline's `CancellationTokenSource`. Both are disposed when the
    `using` blocks end.
- It releases the **admission lease** (`using (prepared.Admission)`) right after the approval gate answers, before
  `execute`.
- It holds **no per-target lock**.

**The callers** hold the rest themselves, around their gateway call:
- **Resource locks and leases:** `WebhookService.ExecuteRunAsync` (`IResourceLocks`, and an AsyncLocal `Holding`) and
  the server lease of `GitDeployService`.
- **Shutdown-drain entries:** only unattended work takes one: `SchedulerService`, `AutoUpdateService` and
  `AutoPatchService`. A UI, MCP or agent run has none.
- **Their own bookkeeping:** for example `AutoUpdateService` keeps `history.CompletedAt` and an "earlier attempt was
  interrupted" guard.

**The UI runner** (`UiMutationRunner.Settle`):
- returns the value for `Failed` with `Error == null`, the "reported failure" branch;
- rethrows a foreign `Error` unchanged;
- throws `MutationFailedException` only for an `OperationCanceledException` while the caller's token is not
  cancelled.

The MCP filter (`McpGuardrailFilter`) depends on `Error` in the same way.

**Audit:**
- the gateway writes through `IMutationAuditSink`, with its outbox and `IAuditRecovery`;
- `Action` is the capability id;
- INV-45 promises exactly one audit event per attempt.

**Discarded tokens:** a text scan of `src/Whiskers` for a discarded token in a lambda (`CancellationToken _`,
`, _) =>`) finds 135 places:
- 96 in `Components/Pages`;
- 8 in `Components/Shared`;
- 31 in services.

Not all of them are gateway actions. The ratchet below counts only gateway actions, and it builds its baseline
itself.

## Behaviour

### 1. The caller waits at most deadline + grace

The gateway awaits the action until whichever comes first:
- the action ends;
- the deadline has passed and the grace period **G** (D1) has run out too.

**An action that ends before the deadline** is handled exactly as today.

**An action that ends after the deadline but within G** (D6) gets these rules:
- If it stopped on the token, it is `Failed` with "its effect is unknown", as today.
- If it returned, its read-back verification runs with a token of its own, bounded by G, and no longer with the
  already-cancelled deadline token.

**Exactly one** of two endings happens, and an atomic exchange decides which:
- the normal outcome;
- abandoned, followed by the late end.

A test checks this at the G boundary.

### 2. Abandoned

When G has run out and the action is still running, the caller gets:
- status `Failed`;
- verification `NotRun`;
- `Abandoned = true` (D2);
- reason code `MutationReasonCodes.Abandoned`;
- as `Error`, a new `MutationAbandonedException`, derived from `OperationCanceledException`, which carries the
  deadline;
- the reason: "'{capability}' ran past its deadline of {deadline} and did not stop; it is still running and its
  effect is unknown — read the current state before retrying."

Each surface reports it as follows:
- **The UI runner** throws `MutationFailedException`, with the new property `Abandoned = true`. This holds for both
  `RunAsync` overloads, so a page never shows success for it.
- **An MCP tool** answers with the failure envelope and the reason code.
- **The automation runner and `TaskExecutor`** record the failure.

The outcome row is written straight away. A `Warning` log line names the operation id, the capability and the
deadline.

The action itself is neither cancelled any further nor killed. It keeps running in the background with everything
point 4 lists.

### 3. The late end is a second phase of the same audit event

When an abandoned action ends, the gateway appends a follow-up phase to the same audit event:
- **The record:** same `EventId`, `OperationId` and `Action` (the capability id), with `Phase = "late_end"`. It is
  written through a new `IMutationAuditSink.AppendLateEndAsync`. A failed write goes to the outbox, like
  `CompleteAsync`.
- **What it holds:**
  - how the action ended: `returned`, `threw` (the exception type only, the message through the usual redaction) or
    `cut off at shutdown`;
  - how long it ran in total;
  - the scope's `Results` and `FailureReason` that the tool reported after the abandon, capped and redacted as today.
- **What stays unchanged:**
  - the first outcome row, because audit rows are append-only;
  - INV-45. It is amended: "exactly one audit event per attempt; an abandoned attempt gets one follow-up phase on
    that event".
- **No verification** runs on the late end. The status stays `Failed`: the effect is unknown, and nobody waits for
  the value any more.
- **An `Information` log line** records the end.

### 4. What stays held until the real end

The gateway answers early. Everything that protects the target must still last until the action actually ends.

**4a. Held by the gateway itself:**
- **The maintenance admission.** A restore keeps waiting for the abandoned run, including for its late-end write
  and the journal finish.
- **The journal claim.** It stays `Running`, and afterwards it is finished as `Failed`, never as `Executed`.
- **The `MutationScope` and the deadline's `CancellationTokenSource`.** They are disposed only in the continuation of
  the late end. Nested `[MutatingOperation]` calls of the running action therefore still find their scope, and
  reports after the abandon reach point 3.

**4b. A target guard owned by the gateway** (new):
- While an abandoned run is in `IAbandonedRuns`, every new mutation is refused fail-closed when its resolved target
  overlaps that run's target:
  - in kind, id and server;
  - or in its `Related` resources.
- The refusal has reason code `MutationReasonCodes.TargetBusy` and the reason "an earlier run on this target ran past
  its deadline and is still running".
- This holds for every principal, the emergency stop included. A refusal changes nothing, which makes it the safe
  side.
- **Effect:** a retry by a UI user, an MCP agent, the scheduler or AutoUpdate cannot pile up background runs on a
  hung host. The number of abandoned runs is bounded by the number of distinct targets.

**4c. Callers wait for the real end before they release what they hold:**
- `MutationOutcome<T>` gets `Task Completion`.
  - For an outcome that is not abandoned, it is already complete.
  - For an abandoned one, it completes after the late end.
- These callers await `Completion` before they release their lease, entry or bookkeeping:
  - `WebhookService`: lock and AsyncLocal `Holding`;
  - `GitDeployService`: server lease;
  - `SchedulerService`, `AutoUpdateService` and `AutoPatchService`: drain entry;
  - `AutoUpdateService`: `history.CompletedAt` and the interruption guard.
- Their own caller, for example the webhook's HTTP answer, does not wait. They wait in the background, within the
  limit of D5.
- UI, MCP and agent runs take no drain entry. The gateway itself takes one for each abandoned run (point 6).

**Release:** neither the emergency stop nor the kill switch releases any of these. Releasing them would let two
writers act on one target.

### 5. Visible while it runs

- **Gauge:** `whiskers_mutations_abandoned_running`.
- ***Whiskers itself* (`/self-status`):** one line per abandoned run, with capability, operation id, its start and
  how long it has run. It shows no arguments and no target text.
- **A run that never ends (D5):** once an abandoned run has gone on longer than the limit L, readiness turns
  `Degraded` with the line "a mutation has been running past its deadline for over L". Whiskers stays usable;
  overlapping targets remain refused.
- **What an Admin can do about it:** fix the host (or restart Whiskers). After a restart, the reconciliation of
  INV-39 reports the run as interrupted.

### 6. Shutdown

- **The drain entry:** the gateway takes one for each abandoned run, so the shutdown drain (30 s budget) waits for it
  like any other work. The gateway learns of the stop through `IShutdownDrain`.
- **What is still running after the budget** is cut off by the stop:
  - the late end is written as `cut off at shutdown` if there is still time;
  - otherwise the journal claim stays `Running`, and the start-up reconciliation reports the operation as interrupted
    (INV-39).

### 7. Limits that stay

- **Request-scoped services (D7).**
  - An MCP tool runs with services of its request (DbContext, `HttpContext`). A Blazor lambda touches its circuit
    (Snackbar, `Progress`).
  - After the abandoned answer the request or the circuit can end. A later access then throws
    `ObjectDisposedException`, and the run ends as `threw`, with a partial effect.
  - The late end records this. The batches move actions off request-scoped services where they find them.
- **Out of scope for this contract:**
  - a hard abort, or killing a process or a connection;
  - AI-trigger runs, LLM streams and `instruct_agent`, which have deadlines of their own (R1-38);
  - background loops outside the gateway;
  - webhook **deliveries** (INV-42).

  The webhook **mutation**, on the other hand, runs through the gateway and falls under 4c.

## The token ratchet

The new architecture test `DeadlineTokenRatchetTests` works on the **compiled IL** of `Whiskers.dll`, not on the
source text. This covers the compiled `.razor` components too.

- **What it finds:** it reuses the scan of `MutationBypassArchitectureTests.UiRunnerLambdas` (`ldstr` capability, the
  lambda via `ldftn`, then the call of the entry point). It is widened to:
  - `IMutationGateway.ExecuteAsync`;
  - every method marked `[MutationEntryPoint]`: `IUiMutationRunner.RunAsync`, the automation runner and
    `TaskExecutor.RunGatedAsync`.

  It covers lambdas, method groups and local functions.
- **When a site is clean:** the action, or the `MoveNext` of its async state machine, passes the token parameter (or
  the field it was hoisted into) as an argument to a call. These do not count as passing it on:
  - `_ = ct`;
  - `ct.ThrowIfCancellationRequested()` alone;
  - `CancellationToken.None` or `default`.
- **Baseline:**
  - `src/Whiskers.Tests/Architecture/deadline-token-baseline.txt` holds one line per site: `<declaring type>|<capability
    id>|<method name of the action>`;
  - for `.razor` components, the type maps to its `.razor` path in the message;
  - a site is identified by its compiler-generated method, not by a count, so one discarding site cannot be swapped
    for another.
- **The test fails in two ways:**
  - a new site that is not in the baseline: the message names the file and the capability, and says to pass the
    token on;
  - a baseline line that no longer matches: the message says to remove it from the baseline. The list can therefore
    only shrink.
- **Outside the scan:**
  - MCP tool methods and agent tools that the reflection invoker calls;
  - whether a service method honours the token.

  The batches see to the latter.

## Batches

- **Size:** one PR per area, with at most about 30 sites:
  1. the container and stack pages;
  2. the server, firewall and host pages;
  3. settings, guardrails, AI operations and keys;
  4. the shared components;
  5. the services (scheduler, vault and others).
- **What each batch does:**
  - Each action passes the token on.
  - A service method without a token gets a new overload or parameter (`CancellationToken ct = default`), whichever
    does not collide with existing overloads (`IDockerService`, R1-41).
- **Safe points:**
  - A service observes the token only before its first change and between atomic steps. It never cancels in the
    middle of a step that leaves a half-applied state:
    - stack down and up;
    - writing a configuration, then reloading it;
    - a firewall rule set;
    - recreating a container.
  - Where a step cannot be split, it either runs to its end with `CancellationToken.None` (and says so in a comment)
    or compensates.
  - Each multi-step service gets one test of this.
- **The batch removes its lines** from the baseline.
- **Target for 1.1.0:** D4.

## Interfaces

| Type | Change |
|---|---|
| `MutationOutcome<T>` | new `bool Abandoned { get; init; }` (default `false`) and `Task Completion` (complete unless abandoned) |
| `MutationAbandonedException` (new) | derives from `OperationCanceledException`; carries `Deadline` |
| `MutationFailedException` | new `bool Abandoned` |
| `MutationReasonCodes` | new `Abandoned`, `TargetBusy` |
| `IMutationAuditSink` | new `AppendLateEndAsync(eventId, operationId, how, duration, results, ct)`; on failure: outbox, like `CompleteAsync` |
| `IAbandonedRuns` (new, singleton) | `Running` (capability, operation id, target, since), `Overlaps(target)`; for `/self-status`, the gauge, readiness and 4b |
| `MutationGateway` | new options `AbandonGrace` (G) and `AbandonedLimit` (L); takes `IShutdownDrain` |
| `IUiMutationRunner` | doc: an abandoned run throws `MutationFailedException` with `Abandoned` |
| Callers in 4c | await `outcome.Completion` before they release |

`MutationStatus` stays unchanged (D2).

## Tests (acceptance)

`AbandonedRunTests` (gateway):
- an action that ignores the token, deadline 100 ms, G 200 ms: the caller gets its answer before 1 s, with `Failed`,
  `Abandoned`, `MutationAbandonedException` and the reason code, and the outcome row is written;
- the repro of the 1.0.0 review (in `docs/review-1.0.0-repros` of the maintainer, written new here) passes;
- an action that stops on the token is `Failed` without `Abandoned`, as today;
- an action that returns after the deadline but within G is verified with its own token, with no false
  "read-back failed";
- abandon and normal end at the G boundary: exactly one of the two endings (repeated run);
- the late end appends exactly one `late_end` phase (`returned`, or `threw` with the type only, plus the scope's later
  results); a failed write lands in the outbox;
- the journal claim stays `Running` until the real end and is then `Failed`, never `Executed`;
- the scope stays usable: a nested `[MutatingOperation]` call after the abandon is not refused by the scope guard;
- 4b: a second mutation of an overlapping target (same id, or via `Related`) is refused with `TargetBusy`, also for
  the emergency stop and for a new operation id; once the run ends, it goes through;
- a restore waits for the abandoned run;
- `IAbandonedRuns`, the gauge and `/self-status` count it while it runs; after L, readiness is `Degraded`;
- shutdown: the drain waits; what still runs is reconciled at the next start.

Further tests:
- **Callers (4c):** webhook, Git deploy, scheduler, AutoUpdate and AutoPatch keep their lease, lock, drain entry or
  history until `Completion`.
- **`UiMutationRunnerTests`:** both `RunAsync` overloads throw `MutationFailedException` with `Abandoned`.
- **MCP filter:** the failure envelope with the reason code.
- **`DeadlineTokenRatchetTests`:**
  - the baseline matches the IL exactly;
  - a fixture with `_ = ct`, `ThrowIfCancellationRequested` alone, `CancellationToken.None` or a method group is
    found;
  - a fixture that passes the token on is not.
- **One safe-point test** per multi-step service in the batches.

Every new test goes into the test manifest.

## Documentation (same PRs)

- `docs/v1/SECURITY_INVARIANTS.md`:
  - **INV-40:** the first sentence "its slot stays held until then" becomes "the caller is answered after deadline +
    G; the run is marked abandoned, keeps what protects its target until its real end, and that end is audited". The
    agent-tools sentence "a mutation is never abandoned there" is qualified: the agent invoker still does not abandon
    a run, but the gateway does after deadline + G. The count of discarding sites goes down with each batch.
  - **INV-45:** the follow-up phase of an abandoned attempt.
- `src/Whiskers/Services/Mutations/README.md`: the deadline section and the audit section. Also the `IUiMutationRunner`
  doc.
- `docs/operations/observability.md`: the gauge, the `/self-status` line and the readiness line.
- `docs/operations/troubleshooting.md`: "ran past its deadline and did not stop", "an earlier run on this target … is
  still running", and what to do.
- Contract 0017 part J: a note pointing here.
- `CHANGELOG.md`.

## Plan triage

- Behaviour 1–7, the ratchet and every batch: `[Claude-prüfbar]`.
- An abandoned run in the real app (for example on a slow host): `[braucht Nutzer]`, D6 in the 1.1.0 plan.

## Open decisions

| | Question | Recommendation | Alternative |
|---|---|---|---|
| D1 | How long is the grace period G after the deadline? | **5 s**, as the host shell's clean-up budget (R1-57) | 0 s, or a share of the deadline |
| D2 | How does the caller see an abandoned run? | **`Failed` with `Abandoned`, a reason code and `MutationAbandonedException`**: every consumer already handles `Failed`, and no enum value or migration is needed | a new `MutationStatus.Abandoned`, which every switch, filter, MCP answer and the audit page must learn |
| D3 | What happens at the late end? | **The audit phase, a log line, the gauge and `/self-status`**: no verification, no notification | an additional notification `mutation_abandoned` |
| D4 | What counts as done for 1.1.0? | **Empty baseline**: every gateway action in the scan passes the token on, with safe points | baseline frozen; the batches go on after 1.1.0 |
| D5 | A run that never ends: the limit L before readiness turns `Degraded` | **15 min**; overlapping targets stay refused, and a restart reconciles | no readiness signal, `/self-status` only |
| D6 | An action that returns after the deadline but within G | **Verify it with its own token bounded by G**: the action completed, so its effect can be checked | `Failed` "ran past its deadline", without verification |
| D7 | Request-scoped services under an abandoned run | **Name the limit**; the batches move actions off request-scoped services | every MCP mutation in its own DI scope, as `AgentToolInvoker` already does (more change in the MCP filter) |
