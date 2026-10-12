# Services/Docker/Budget

The **per-server load cap** (Plan-0001 WP3): how much Whiskers is allowed to ask of one server at a time,
counted across every caller.

Before this existed, each background loop had its own timeout and no idea what the other four were doing on
the same host. The server sees the sum. On 2026-08-26 that sum was thirteen concurrent full-log scans against
a two-core machine, and it stayed that way for six days. Fixing the loop that started it would have left the
next loop free to repeat it — so the limit lives at the one point every Docker call passes through,
[`DockerConnectionManager.ExecuteAsync`](../DockerConnectionManager.cs), rather than in the loops.

**A new loop is therefore bounded on the day it is written**, without its author having to know this exists.

## Files

| File | Purpose |
|---|---|
| `IServerBudget.cs` | The seam: `RunAsync` (waits for a slot, then runs), `BackgroundScope` (marks the current async flow as background work), `Snapshot`/`SnapshotAll` (what is running — the raw material for the self-metrics in SP-3). |
| `ServerBudget.cs` | Two `SemaphoreSlim` lanes per server, created on first sight of that server. Counts starts, wait time and peak wait — the numbers that say whether the limit is right. |
| `IServerCircuitBreaker.cs` / `ServerCircuitBreaker.cs` | Stops calling a server that has stopped answering: Closed → Open after a run of transport failures → HalfOpen after a cooldown, one probe → Closed on success. Only transport failures and our own timeouts count; "no such container" says nothing about the host. |

## The circuit is never silent

An open circuit means Whiskers has **stopped looking at that host**. Unannounced, that is indistinguishable
from "all quiet" — the exact confusion that let the 2026-08-26 incident run for six days. So every transition
sends a notification (`server_throttled` / `server_throttling_ended`), and the wording says plainly that this
is Whiskers throttling *itself* and that the server is not being checked meanwhile.

Exactly one notification per transition, though: a host that stays down produces one event, not one per
cooldown. An alert channel that repeats itself is an alert channel people mute — and a muted channel is the
same blindness by another route.

## Two lanes, not one queue

Background work and anything a human is waiting for are kept apart on purpose. Sharing a queue means a CVE
scan holding the budget makes the UI look frozen — and a frozen UI reads as "the server is down", which is
the opposite of what a monitoring tool should say. Callers mark background work with `BackgroundScope()`;
anything unmarked counts as **interactive**, which is the safe default: mistaking a loop for a user costs a
slot, mistaking a user for a loop costs responsiveness.

## Defaults

Four concurrent background calls and four interactive, per server. Sized for the smallest host in a typical
fleet — the two-core machine the incident happened on — **not** for a development box: a limit tuned on an
eight-core laptop would permit exactly the load that caused it.

Configure via `ServerBudget:BackgroundConcurrency` / `:InteractiveConcurrency`, with per-server overrides
under `ServerBudget:PerServer:<serverId>:` — see
[`Configuration/ServerBudgetSettings.cs`](../../../Configuration/ServerBudgetSettings.cs).

## What is deliberately not budgeted

The budget covers the steady background read traffic only. Mutating and long-running operations (restart,
recreate, exec, pulls) bypass it on purpose: when a server is struggling, the circuit would otherwise cut the
operator off from exactly the repair they need. Single-flight applies to background callers only.
`DockerBudgetCoverageTests` pins the list of bypasses as a ratchet — a new one has to be added there with a reason.

## How you would notice it is wrong

A cap has two failure modes and only one of them is loud:

- **Too high** — the server suffers. Visible on the host: `dockerd` CPU, and the open-descriptor count from
  the incident report.
- **Too low** — the loops starve, nothing is checked, and everything reports "fine". This is the quiet one.
  Watch the wait time in `Snapshot` (a median above a couple of seconds means the budget is the bottleneck,
  not the server) and, once SP-3 lands, the age of each loop's last successful cycle.

`ServerBudgetTests` pins the behaviour, including a raised-limit case that proves the cap is actually
measured rather than assumed.
