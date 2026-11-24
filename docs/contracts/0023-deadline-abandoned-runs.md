# Contract 0023 — Runs past their deadline, and the token ratchet (INV-40)

Status: **Draft** · 24 November 2026, waiting for the maintainer's acceptance. It implements decision A2 of the
1.1.0 plan (24 November 2026): the deadline stays cooperative and there is no hard abort. A run that outlives its
deadline is marked `abandoned` and audited. A test forbids new places that discard the token, and the existing ones
are fixed in batches. The open decisions D1–D4 are at the end, each with a recommendation.

## Purpose

INV-40 says that every execution has a deadline that cancels its token. Contract 0017 part J made the wording
precise: the deadline is cooperative, and an action that ignores the token runs to its end. The 1.0.0 review counted
122 runner lambdas that discard the token (`(ResolvedContainer c, CancellationToken _) => Service.DoAsync(c.Id)`).

For those actions the deadline has no effect at all:
- the caller waits until the action ends, however long that takes;
- when the action finally returns, the run is recorded as `Executed`, as if it had kept its deadline;
- the UI page, the MCP client or the automation hears nothing about the deadline.

This contract makes a run past its deadline visible and bounds how long the caller waits. It also stops the number
of discarded tokens from growing, and brings it down to zero for 1.1.0.

## Current state (verified at `7d5bbe5e`)

- `MutationGateway.ExecuteAsync` links the caller's token with `capability.Deadline` (`CancelAfter`) and awaits
  `execute(context, deadline.Token)` without any bound.
  - An `OperationCanceledException` from the deadline gives `Failed`: "exceeded its deadline … its effect is unknown".
  - An action that does not observe the token is awaited to its end, then verified and recorded as usual.
- The operation journal's claim, the admission lease, the resource locks and the `MutationScope` are released only
  after `execute` returns.
- `IUiMutationRunner` documents this: "this call waits for it, and its slot stays held until then".
- A scan of `src/Whiskers` for a discarded token in a lambda (`CancellationToken _`, `, _) =>`) finds 135 places:
  - 96 in `Components/Pages`, 8 in `Components/Shared`;
  - 31 in services (scheduler, vault, mutations and others).

  Not every one of them is a gateway action. The ratchet below counts only the gateway actions, and it builds its
  baseline itself.

## Behaviour

### 1. The caller waits at most deadline + grace

The gateway awaits the action until whichever comes first:
- the action ends;
- the deadline has passed and the grace period **G** (D1) has run out as well.

If the action ends in time, everything stays as it is today. That holds for a cancellation at the deadline too: an
action that stops on the token is still `Failed` with "its effect is unknown".

### 2. Abandoned

When G has run out and the action is still running:
- **What the caller gets:**
  - status `Failed`;
  - verification `NotRun`;
  - the new flag `Abandoned = true` (D2);
  - the reason: "'{capability}' ran past its deadline of {deadline} and did not stop; it is still running and its
    effect is unknown — read the current state before retrying."
- **How each surface reports it:**
  - the UI runner throws `MutationFailedException` (already documented);
  - an MCP tool answers with the failure;
  - the automation runner records the failure.
- **Audit:**
  - the intent row gets this outcome straight away;
  - a `Warning` log line names the operation id, the capability and the deadline.
- **What the action holds:** it is not cancelled any further and not killed. It keeps running in the background with
  everything it holds (point 4).

### 3. The late end is audited

When an abandoned action ends:
- **The audit row:** the gateway appends one audit row with the action `mutation.abandoned_end` under the same
  operation id. It records:
  - how the action ended: `returned`, `threw` (the exception type only, the message through the usual redaction) or
    `cut off at shutdown`;
  - how long it ran in total.
- **What stays as it is:** the first outcome row is never changed, because audit rows are append-only.
- **No verification:** no read-back verification runs on the late end (D3). The status stays `Failed`: the effect is
  unknown, and nobody waits for the value any more.
- **Logging:** an `Information` log line records the end.

### 4. What stays held until the real end

The following stay with the running action and are released only when it actually ends:
- the operation journal's claim. It stays `Running` and is then finished as `Failed`, never as `Executed`. A retry
  under the same operation id therefore stays refused until then;
- the admission lease and the resource locks. A second mutation of the same target waits or is refused as today,
  while the first one still runs;
- the server budget slot;
- the shutdown-drain entry;
- the `MutationScope`.

Neither the emergency stop nor the kill switch releases any of them, because releasing them would allow two writers
on one target at the same time.

### 5. Visible while it runs

- **Gauge:** `whiskers_mutations_abandoned_running` counts the abandoned actions that are still running.
- ***Whiskers itself* (`/self-status`):** one line per abandoned action, with its capability, operation id and start
  time. It shows no arguments and no target text.
- **Readiness:** the readiness check stays unchanged. A running abandoned action is a fact, not a fault of the
  instance.

### 6. Shutdown

- **Drain:** the shutdown drain waits for abandoned actions within its existing budget of 30 s, like any other
  running work.
- **What the stop cuts off:**
  - the action is cut off by the stop;
  - the late-end audit row is written with `cut off at shutdown` if there is still time;
  - otherwise the journal claim stays `Running`, and the existing start-up reconciliation reports the operation as
    interrupted (INV-39).

### 7. Not part of this contract

- A hard abort, or killing a process or a connection.
- AI-trigger runs, LLM streams, webhooks and `instruct_agent`. They already have deadlines of their own (R1-38,
  INV-42).
- Background loops that do not run through the gateway.

## The token ratchet

The new architecture test `DeadlineTokenRatchetTests` scans `src/Whiskers` (`.cs` and `.razor`).

- **What it finds:** every call of `IMutationGateway.ExecuteAsync` and of a method marked `[MutationEntryPoint]` (`IUiMutationRunner.RunAsync`,
  the automation runner, `TaskExecutor`) whose action lambda discards its `CancellationToken`. The
  parameter is either `_` or never used in the lambda's body.
- **Baseline:** `src/Whiskers.Tests/Architecture/deadline-token-baseline.txt` holds one line per site in the form
  `<path>|<capability id>|<n>`, where `n` counts that capability's sites in that file. The line has no line number,
  so unrelated edits do not move it.
- **A new site that is not in the baseline** fails the test. The message names the file and the capability, and says
  to pass the token on.
- **A baseline line that no longer matches** fails too, with "remove it from the baseline". The list can therefore
  only shrink.
- **A lambda that passes the token on** (`(ResolvedContainer c, CancellationToken ct) => Service.DoAsync(c.Id, ct)`)
  is clean. Whether the service method itself honours the token is not part of the scan; the batches see to that.

## Batches

- **Size:** one PR per area, at most about 30 sites each:
  1. the container and stack pages;
  2. the server, firewall and host pages;
  3. settings, guardrails, AI operations and keys;
  4. the shared components;
  5. the services (scheduler, vault, and so on).
- **What a batch does:**
  - each lambda passes the token on;
  - where the service method has no token parameter, it gets `CancellationToken ct = default`, which is passed down
    to the I/O it does (Docker, SSH, HTTP, database, file);
  - the batch removes its lines from the baseline.
- **Target for 1.1.0:** an empty baseline (D4).

## Interfaces

| Type | Change |
|---|---|
| `MutationOutcome<T>` | new `bool Abandoned { get; init; }`, default `false` |
| `MutationGateway` | new constructor option `TimeSpan abandonGrace` (default G); an `internal` hook for tests |
| `IAuditLogService` / audit actions | new action `mutation.abandoned_end` (constant in the audit action list) |
| `IAbandonedRuns` (new, singleton) | `IReadOnlyList<AbandonedRun> Running` (capability, operation id, since) for `/self-status` and the gauge |
| `IUiMutationRunner` | doc only: a run past its deadline + G throws `MutationFailedException` with `Abandoned` |

`MutationStatus` stays unchanged (D2).

## Tests (acceptance)

`AbandonedRunTests`:
- an action that ignores the token, deadline 100 ms, G 200 ms: the caller gets its answer before 1 s, with `Failed`
  and `Abandoned`, and the outcome row is written;
- the review's repro (`A_delegate_that_ignores_the_token_is_still_ended_by_the_capability_deadline`) becomes a
  regular test and passes;
- an action that stops on the token is `Failed` without `Abandoned`, exactly as today;
- an action that ends after the abandon writes exactly one `mutation.abandoned_end` row: `returned`, or `threw` with
  the type only;
- the journal claim stays `Running` until the real end and is then `Failed`, never `Executed`;
- a second mutation of the same target is held or refused while the abandoned one runs;
- `IAbandonedRuns` and the gauge count the run while it runs and drop it after it ends;
- shutdown with an abandoned action still running: the drain waits, and the claim is reconciled at the next start.

Further tests:
- `DeadlineTokenRatchetTests`: the baseline matches the source exactly; a new discarding lambda in a test fixture is
  found, and a passing one is not.
- `UiMutationRunnerTests`: `MutationFailedException` for an abandoned run.

Every new test goes into the test manifest.

## Documentation (same PRs)

- `docs/v1/SECURITY_INVARIANTS.md` INV-40:
  - the limit "an action that ignores the token runs to its end" gets the addition "the caller is answered after
    deadline + G, the run is marked abandoned and its end is audited";
  - the count of discarding sites goes down with each batch, and the limit sentence goes once the baseline is empty.
- `src/Whiskers/Services/Mutations/README.md` (deadline section) and the `IUiMutationRunner` doc.
- `docs/operations/observability.md`: the gauge and the `/self-status` line.
- `docs/operations/troubleshooting.md`: "ran past its deadline and did not stop", and what to do (read the state,
  wait for the late end in the audit log).
- Contract 0017 part J: a note pointing here.
- `CHANGELOG.md`.

## Plan triage

- Behaviour 1–6, the ratchet and every batch: `[Claude-prüfbar]`.
- An abandoned action in the real app (for example a slow host): `[braucht Nutzer]`, D6 in the 1.1.0 plan.

## Open decisions

| | Question | Recommendation | Alternative |
|---|---|---|---|
| D1 | How long is the grace period G after the deadline? | **5 s**, the same as the clean-up budget of the host shell (R1-57) | 0 s (abandon at the deadline), or a share of the deadline |
| D2 | How does the caller see an abandoned run? | **`Failed` plus the flag `Abandoned`**: every consumer already handles `Failed`, and no enum value, migration or filter changes | a new `MutationStatus.Abandoned`, which every switch, filter, MCP answer and the audit page must learn |
| D3 | What happens at the late end? | **Audit row, log line, gauge and `/self-status` only**: no verification and no notification | an additional notification `mutation_abandoned` to the channels |
| D4 | What counts as done for 1.1.0? | **Empty baseline**: every gateway action passes the token on, and the INV-40 limit sentence goes | baseline frozen; the batches go on after 1.1.0 |
