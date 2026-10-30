# Observability

Two separate things live here: what Whiskers **did** (governance recording, below) and whether Whiskers is
**still working at all** (`SelfMetrics/` and `ScanSupervisor.cs`).

## Is Whiskers still working?

| File | Purpose |
|---|---|
| `SelfMetrics/` | Loop health per (loop, server): last success, last attempt, cycle duration, failures, and **skips with a reason**. Exported as `whiskers_self_*` on `/metrics`. See [`SelfMetrics/ISelfMetrics.cs`](SelfMetrics/ISelfMetrics.cs). |
| `SelfMetrics/SelfMetricsRecorder.cs` | Writes those numbers to `SelfMetricSamples` once a minute, restores them on boot, and prunes them on their own retention. |
| `SelfMetrics/SelfStatusPresenter.cs` | The judgement behind `/self-status` — stalled vs healthy vs skipped vs unjudged — using the supervisor's threshold rather than its own copy. |
| `SelfMetrics/ActionTimeline.cs` | "What happened at 14:02?" — human actions and Whiskers' own decisions on one UTC timeline. |
| `ScanSupervisor.cs` | Watches the watchers: raises `monitoring_stalled` when a loop has not completed a cycle for a server in three of its own intervals — whatever the cause. |
| `ILoopSuspensionService.cs` / `LoopSuspensionService.cs` | The emergency stop: pause one server's background checks. Announced, time-bounded, fail-open, not persisted across restarts (deliberately — see the file). |
| `SuspensionReminder.cs` | Keeps saying that a paused server is still unwatched, every 24 h, for as long as it stays paused. |
| `ServerSuspendedException.cs` | What a background caller gets when it reaches a paused server. Its own type so a pause is never counted as a failure. |

### Why the numbers are on disk

History is the smaller half of it. The larger half: after a restart the in-memory view is empty, and an empty
"last success" is indistinguishable from "never succeeded". A supervisor facing that has only bad options —
alarm on every restart, or stay quiet about fresh loops, which is exactly the window in which a bad deploy has
most likely broken something.

So `SelfMetricsRecorder` restores the last known success on boot, under three rules that tests hold in place:

- **A live reading always beats the one from disk.** A short-cadence loop can complete a cycle before the
  restore reaches it, and a stale timestamp winning there would manufacture the false alarm this prevents.
- **Nothing older than a week is restored.** Beyond that the reading says nothing about now, and a loop dead
  for a month would come back looking recently alive.
- **A restart must not hide a real stall.** There is a test for that direction specifically — a restore that
  made every restart look healthy would be worse than no restore at all.

The sampling costs one database write per loop and server per minute and **zero Docker calls** — pinned by a
test, because a self-measurement that adds load to what it measures is the same mistake it exists to reveal.

### The emergency stop and its limits

On 2026-08-26 the load on the host was Whiskers itself, and the only way to stop it was SSH on the affected
server — past the tool causing the problem. `ILoopSuspensionService` is the way back that does not require
reaching the machine being hurt.

The check sits in `DockerConnectionManager.GetClientAsync`, the one point every Docker call passes through, so
a new background loop cannot forget to ask. Only background traffic is turned away: interactive access keeps
working, because an operator pauses a server in order to look at it.

Two rules hold the switch honest, and both are enforced by tests rather than by convention:

- **`ScanSupervisor` must not know this service exists.** It reports that nothing is being checked, and a
  supervisor that can be silenced by the switch it supervises is a blindfold with a label on it. That is why
  the 24-hour reminder lives in a *separate* service, `SuspensionReminder`, which may read the pauses.
- **A pause is never silent and never open-ended by accident.** It is announced when set and when lifted, it
  lapses on its own, and while it stands the reminder keeps repeating that the server is unwatched.

Whiskers exported the container inventory of the whole fleet and not one number about itself. On 2026-08-26
the log monitor wrote "timed out after 15s" into every cycle for six days; nothing counted it, so nothing
could act on it, and the host sat at 98% CPU until a person happened to look at a dashboard.

Two design points are worth keeping:

- **The age of the last success is the number that matters**, not a failure counter. Failures are only
  produced while something still happens; a loop that has stopped produces nothing at all.
- **A skipped server must stay visible.** Four loops filter Kubernetes hosts out. Absent from the metrics,
  "this loop does not cover that server" reads exactly like "that server has nothing wrong with it".

The supervisor makes a deliberately *weaker* claim than the specific guards — it only knows that nothing has
been reported — and is therefore far harder to defeat. It must never become suppressible by the mechanisms it
supervises: a switch that can silence the alarm about being silent is not a switch, it is a blindfold.

## What Whiskers did

Governance recording for the agent/MCP layer, **every** tool call Whiskers executes is captured here so it can be reviewed in the **Agent-History** dashboard (`/agent-history`).

## What gets recorded

For each call: timestamp, actor + actor type, tool name, required permission level, secret-redacted parameters, the guardrail verdict (`allow` / `confirm` / `deny`), success, duration, an optional result summary, server id and any error. The entity is [`McpToolCallEntity`](../../Models/McpToolCall.cs); it lives in the `McpToolCalls` table of [`MetricsDbContext`](../Persistence/MetricsDbContext.cs) with the same retention as the audit log (`Audit:RetentionDays`, default 365 days, at least 30 — R1-23; pruned in [`MetricsCollectorService`](../Metrics/MetricsCollectorService.cs)).

## Two recording points (so nothing is missed)

| Path | Recorded by | Actor type |
|---|---|---|
| In-process agent (web chat, `instruct_agent`, AI triggers) | [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs): at every return path, with full params + verdict + result + duration | `agent-web`, `agent-mcp`, `trigger` |
| External / direct MCP `tools/call` (e.g. Claude Code calling a tool itself) | [`McpCallLogMiddleware`](../../Mcp/McpCallLogMiddleware.cs): sniffs the JSON-RPC body on `POST /mcp` | `mcp-direct` |

## Files

| File | Purpose |
|---|---|
| `McpCallLogStore.cs` | `IMcpCallLogStore` + `McpCallLogStore`, records and queries tool-call entries. Writes through a scoped `MetricsDbContext` (safe to call from singletons). Query filters: actor, tool, verdict, writes-only, since. |

## Related

- Dashboard: [`../../Components/Pages/AgentHistory.razor`](../../Components/Pages/AgentHistory.razor)
- Secret redaction: [`../../Utils/SecretRedactor.cs`](../../Utils/)
- Guardrail verdicts: [`../Guardrails/`](../Guardrails/)

## Decisions worth keeping

- **The emergency stop does not survive a restart** — otherwise the pause outlives its reason.
- **No automatic pause when a server's circuit opens** — the circuit already blocks the traffic; a second
  mechanism would only hide it.
- **No staggered resume** after a pause — measured: resuming does not cause a request storm.
- **Action outcomes:** "not measurable" never counts as success, and a success rate is shown only from five
  judged outcomes on. Automatic undo stays off until the observed rates justify it.
