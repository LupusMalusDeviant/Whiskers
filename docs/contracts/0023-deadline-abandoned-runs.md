# Contract 0023 — Runs past their deadline, and the token ratchet (INV-40)

Status: **Draft** · 24 November 2026, revised the same day after review round 1. It waits for the maintainer's
acceptance and implements decision A2 of the 1.1.0 plan (24 November 2026):
- the deadline stays cooperative, and there is no hard abort;
- a run that outlives its deadline is marked `abandoned` and audited;
- a test forbids new places that discard the token, and the existing ones are fixed in batches.

The open decisions D1–D8 are at the end, each with a recommendation.

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
- **Resource locks and leases:** `WebhookService.ExecuteRunAsync` (`ResourceLocks`, and an AsyncLocal `Holding`) and
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
  `RunAsync` overloads, so a page never shows success for it. `UiMutationRunner.Settle` gets an explicit `Abandoned`
  branch that comes before the `OperationCanceledException` branch. A page token cancelled at the same moment
  therefore does not let the inner exception through. The implementation also checks that no unfiltered
  `catch (OperationCanceledException)` sits above a `throw outcome.Error`. Today there is one such rethrow
  (`AutoUpdateService`), and its catches are correct.
- **An MCP tool** answers with the failure envelope and the reason code.
- **The automation runner and `TaskExecutor`** record the failure.

The outcome row is written straight away. A `Warning` log line names the operation id, the capability and the
deadline.

The action itself is neither cancelled any further nor killed. It keeps running in the background with everything
point 4 lists.

### 3. The late end is a further version of the same audit event

`AuditLog.EventId` is unique. One event is one row, and its outcome is written today as the next version of that row
in the chain (`AuditChain.UpdateAsync`). The late end follows the same pattern.

When an abandoned action ends, the gateway writes **one further chain version of the same row**:
- **The record:** same `EventId`, `OperationId` and `Action` (the capability id), with `Phase = "late_end"`. The
  status `Failed` is kept, and the late-end fields are added to the details. It is written through a new
  `IMutationAuditSink.CompleteLateEndAsync`.
- **No second row:** the late end is never written as a second row with the same `EventId`. The unique index would
  swallow it silently.
- **A failed write:** it goes to `IAuditRecovery`, through a new `LateEndNotWritten`, like `OutcomeNotWritten` for
  the outcome. It is kept and written again, and the `audit` health check shows it as incomplete until then.
- **What it holds:**
  - how the action ended: `returned`, `threw` (the exception type only, the message through the usual redaction) or
    `cut off at shutdown`;
  - how long it ran in total;
  - the scope's `Results` and `FailureReason` that the tool reported after the abandon, capped and redacted as today.
- **What stays unchanged:**
  - the outcome version. The chain keeps it, and the late end is a new version after it, never a rewrite;
  - INV-45: still exactly one audit event per attempt. The amendment says that an abandoned attempt gets one further
    version (`late_end`) of that event.
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
- **Where it is checked:** in the gateway's pipeline after the target is resolved and before the intent row. Its
  refusal is the attempt's single INV-45 event (`Denied`), recorded with the target as resolved.
- **What it refuses:** while an abandoned run is in `IAbandonedRuns`, a new mutation is refused fail-closed when its
  resolved target overlaps that run's target. Targets overlap in three cases:
  - same kind, id and server;
  - one target's `Related` resources hold the other;
  - **one of them is a host-level or server-level target** (kind `host` or `server`, for example `host.exec`): such a
    target overlaps every target on that server.
- **The refusal:** reason code `MutationReasonCodes.TargetBusy`, and the reason "an earlier run on this target ran
  past its deadline and is still running".
- **Who it applies to:** every principal, with two exceptions.
  - **The run's own nested calls.** A call is not refused when the chain of **open `MutationScope` objects** it runs
    in (parents with `IsClosed == false`) contains the abandoned operation. Otherwise an abandoned compose run would
    fail half-way at its own next container call.
    - The exemption never reads `request.ParentOperationId`.
    - The gateway clears a `ParentOperationId` supplied by the caller when no enclosing scope is open.
  - **The two kill-switch toggles.**
    - Today the switch is turned on only through `guardrails.save`, which writes the whole edited policy, and turned
      off through `guardrails.kill_switch_off`.
    - This contract adds `guardrails.kill_switch_on`. It changes only `ReadOnlyMode`, like `DisableKillSwitchAsync`,
      and the Guardrails page uses it for switching on (D8).
    - Only `guardrails.kill_switch_on` and `guardrails.kill_switch_off` are exempt, never `guardrails.save`.
    - **A late write never releases the brake.** A `guardrails.save` that ends after its abandon cannot change
      `ReadOnlyMode`: the store keeps the stored value unless the save was started after the last toggle (a version
      compare-and-set on the policy). Without that rule, an older snapshot finishing late would switch the brake off
      silently.

  Every other refusal changes nothing, so the guard errs on the safe side.
- **Remedy:** an abandoned host- or server-level run blocks every mutation on that server, `container.stop`
  included, until it ends. The operator text names the remedy beside the refusal: fix the host, or restart Whiskers
  to clear it (D5).
- **Effect:** a retry by a UI user, an MCP agent, the scheduler or AutoUpdate cannot pile up background runs on a
  hung host. The number of abandoned runs is bounded by the number of distinct targets.

**4c. Callers wait for the real end before they release what they hold:**
- **`Completion`:** `MutationOutcome<T>` gets `Task Completion`.
  - It is complete once the run **and every run nested in it** have really ended:
    - for a run that was not abandoned and has no abandoned nested run, it is already complete;
    - otherwise it completes after its own late end (if it was abandoned) and after every nested `Completion`.
  - It never faults and is never cancelled, so awaiting it cannot throw.
- **Who awaits it:** these callers await `Completion` before they release their lease, entry or bookkeeping:
  - `WebhookService`: lock and AsyncLocal `Holding`;
  - `GitDeployService`: server lease;
  - `SchedulerService`, `AutoUpdateService` and `AutoPatchService`: drain entry;
  - `AutoUpdateService`: `history.CompletedAt` and the interruption guard.
- **No release before the real end:** a caller never releases before `Completion`, however long that takes. The
  limit L of D5 only raises the readiness signal. Their own caller, for example the webhook's HTTP answer, does not
  wait; the release waits in the background.
- **Drain entries:** UI, MCP and agent runs take no drain entry of their own. For a top-level run, the gateway takes
  one when execution starts (point 6).

**4d. Nested runs.** An outer run whose action has returned while a run nested in it is abandoned works like this:
- **Its outcome** is decided when its own action returns. The outer caller is not blocked for the nested runs.
- **Its verification** waits until every nested `Completion`, bounded by G after the outer action returned. If the
  nested runs have not ended by then, the verification is `NotVerifiable` with the reason "a nested run is still
  running past its deadline". A half state is never read as the effect.
- **Its release moves to the background** and happens only after every nested `Completion`. That covers its scope
  (so the inner action keeps its scope chain), its journal claim, its maintenance admission (so a restore cannot swap
  the database under the inner run) and its drain entry.
- **Its `Completion`** follows 4c, so a 4c caller of the outer run keeps its lease, lock or history until the nested
  run has ended too.
- **The outer run is not marked abandoned** for this. Only a run whose own action is still running after deadline + G
  is abandoned.

**Release:** neither the emergency stop nor the kill switch releases any of these. Releasing them would let two
writers act on one target.

### 5. Visible while it runs

- **Gauge:** `whiskers_mutations_abandoned_running`.
- ***Whiskers itself* (`/self-status`):** one line per abandoned run, with capability, operation id, its start and
  how long it has run. It shows no arguments and no target text.
- **A run that never ends (D5):** once an abandoned run has gone on longer than the limit L, readiness turns
  `Degraded` with the line "a mutation has been running past its deadline for over L". Whiskers stays usable;
  overlapping targets remain refused.
- **What an Admin can do about it:** fix the host, or restart Whiskers. After a restart, the reconciliation of
  INV-39 reports the run as interrupted.
- **A late end that was never written.** `LateEndNotWritten` is kept in memory, like `OutcomeNotWritten`. The
  `audit` health check flags the gap only while the process lives; after a restart the late end is gone. What then
  counts is the outcome row (with `Abandoned`) together with the journal (`Failed` or `Interrupted`).

### 6. Shutdown

- **The drain entry:** for a top-level run, the gateway takes the entry when execution starts, not at the abandon.
  The shutdown drain (30 s budget) then waits for a running or abandoned run like any other work.
- **A nested call** takes no entry: the outer run's entry counts for it, the same pattern as the maintenance
  admission. A scheduled compose run admitted before the stop can therefore finish its nested calls during the drain.
- **A top-level call after stopping has begun.** `IShutdownDrain.TryEnter` gives no entry, so the call is refused.
  - **This is new behaviour.** Today UI, MCP and agent mutations are not refused while Whiskers stops, for up to 45 s.
  - The refusal is the attempt's single INV-45 event (`Denied`), with reason code `MutationReasonCodes.ShuttingDown`.
    The database is not being replaced, so the refusal can be recorded.
  - INV-40 and the CHANGELOG name the change.
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
  lambda via `ldftn` or `ldvirtftn`, then the call of the entry point). It is widened to:
  - `IMutationGateway.ExecuteAsync`;
  - every method marked `[MutationEntryPoint]`: `IUiMutationRunner.RunAsync`, the automation runner and
    `TaskExecutor.RunGatedAsync`.

  It covers lambdas, method groups and local functions.
- **When a site is clean:** the action, or the `MoveNext` of its async state machine, passes the token parameter (or
  the field it was hoisted into, or a local or hoisted local copied from it) as an argument to a call. These do not
  count as passing it on:
  - `_ = ct`;
  - `ct.ThrowIfCancellationRequested()` alone;
  - `CancellationToken.None` or `default`.
- **Accepted limit:** any call that takes the token makes the site clean, even `Task.Delay(0, ct)` followed by a call
  without it. Reviews catch that; the ratchet does not.
- **Baseline:**
  - `src/Whiskers.Tests/Architecture/deadline-token-baseline.txt` holds one line per enclosing user method and
    capability: `<file>|<enclosing method>|<capability id>|<count>`.
    - The file is the `.razor` or `.cs` path.
    - The enclosing method is taken from the `<X>` prefix of the generated name, or from the PDB sequence points.
  - Compiler-generated names such as `<OnSaveAsync>b__42_0` are not part of the key. They change with unrelated edits,
    and then every break would become "regenerate the baseline".
  - **Residual risk, accepted:** inside the same method and capability, one discarding site can be swapped for another.
    Lambdas written in `.razor` markup all sit in `BuildRenderTree`, and overloads with the same name merge. For a
    call site inside an async method, the enclosing method is the one named by its state-machine type
    (`<OnSave>d__N`), never `MoveNext`.
- **The test fails in three ways:**
  - **A new site:** a method and capability that is not in the baseline, or a count above the baseline. The message
    names the file and the capability, and says to pass the token on.
  - **A count below the baseline:** the message says to lower the line.
  - **A line that no longer matches:** the message says to remove it.

  The list can therefore only shrink.
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
| `MutationReasonCodes` | new `Abandoned`, `TargetBusy`, `ShuttingDown` |
| `CapabilityCatalog` | new `guardrails.kill_switch_on` (changes only `ReadOnlyMode`; Admin, as `kill_switch_off`) |
| `GuardrailStore` | a `guardrails.save` cannot change `ReadOnlyMode` unless it started after the last toggle (version compare-and-set) |
| `IMutationAuditSink` | new `CompleteLateEndAsync(auditId, operationId, how, duration, results, ct)`: a further chain version of the event's row (`Phase = "late_end"`), never a second row |
| `IAuditRecovery` | new `LateEndNotWritten(...)`: kept and written again, like `OutcomeNotWritten` |
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
- the late end writes exactly one further chain version of the same row (`late_end`: `returned`, or `threw` with the
  type only, plus the scope's later results). There is no second row, and the outcome version stays in the chain;
- a failed late-end write is kept by `IAuditRecovery` and written again;
- the journal claim stays `Running` until the real end and is then `Failed`, never `Executed`;
- the scope stays usable: a nested `[MutatingOperation]` call after the abandon is not refused by the scope guard;
- **4b:**
  - a second mutation of an overlapping target (same id, via `Related`, or a host or server target on the same
    server) is refused with `TargetBusy`, also under the emergency stop and under a new operation id; once the run
    ends, it goes through;
  - the refusal is the single `Denied` event;
  - `guardrails.kill_switch_on` and `guardrails.kill_switch_off` go through while a `guardrails.save` is abandoned;
    a `guardrails.save` does not;
  - the brake is engaged during an abandoned save, and the save then ends late: the brake stays on;
  - a request with a forged `ParentOperationId` equal to the abandoned run and no open scope is refused with
    `TargetBusy`;
- **4b and 4d:** an abandoned compose run makes a nested container call after the abandon, and the call is not
  refused;
- **4d:**
  - an inner run is abandoned and the outer one returns. The outer caller gets its answer at once. The outer scope,
    journal claim, maintenance admission and `Completion` stay until the inner `Completion`, the outer verification
    is `NotVerifiable` if the inner run does not end within G, and a restore waits;
  - a webhook whose outer mutation is not abandoned, but whose nested run is, keeps its server lock until the nested
    run ends;
- **shutdown:**
  - a nested call during the drain is not refused;
  - a top-level UI call after stopping has begun is refused with `ShuttingDown` and recorded;
- a restore waits for the abandoned run;
- `IAbandonedRuns`, the gauge and `/self-status` count it while it runs; after L, readiness is `Degraded`;
- shutdown: the drain waits; what still runs is reconciled at the next start.

Further tests:
- **Callers (4c):** webhook, Git deploy, scheduler, AutoUpdate and AutoPatch keep their lease, lock, drain entry or
  history until `Completion`.
- **`UiMutationRunnerTests`:** both `RunAsync` overloads throw `MutationFailedException` with `Abandoned`, also when
  the page's token is cancelled at the same moment.
- **MCP filter:** the failure envelope with the reason code.
- **`DeadlineTokenRatchetTests`:**
  - the baseline matches the IL exactly;
  - fixtures that are found:
    - `_ = ct`;
    - `ThrowIfCancellationRequested` alone;
    - `CancellationToken.None`;
    - a method group, and a virtual method group (`ldvirtftn`);
  - fixtures that are not found:
    - a lambda that passes the token on;
    - one that passes it on through a local;
  - an unrelated lambda added to the same type leaves the baseline valid.
- **One safe-point test** per multi-step service in the batches.

Every new test goes into the test manifest.

## Documentation (same PRs)

- `docs/v1/SECURITY_INVARIANTS.md`:
  - **INV-40:** the first sentence "its slot stays held until then" becomes "the caller is answered after deadline +
    G; the run is marked abandoned, keeps what protects its target until its real end, and that end is audited". The
    agent-tools sentence "a mutation is never abandoned there" is qualified: the agent invoker still does not abandon
    a run, but the gateway does after deadline + G. The count of discarding sites goes down with each batch.
  - **INV-45:** the further version (`late_end`) of an abandoned attempt, and the new `TargetBusy` and `ShuttingDown`
    refusals.
  - **INV-40:** the refusal of top-level mutations while Whiskers stops (new behaviour).
- `docs/operations/` where the kill switch is described: `guardrails.kill_switch_on`, and that a late save never
  releases the brake.
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
| D8 | How is the kill switch switched on while a `guardrails.save` is abandoned? | **A new capability `guardrails.kill_switch_on`** that changes only `ReadOnlyMode`, exempt from the guard; a late save never lowers `ReadOnlyMode` | no exemption: switching on waits until the abandoned save ends (a local file write, rarely long) |
