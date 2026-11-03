# Host load

The rules that judge a **host**, not a container.

| File | Purpose |
|---|---|
| `HostSample.cs` | One reading, plus the CPU-scale reconciliation that makes the two figures comparable. |
| `HostLoadEvaluator.cs` | Sustained host CPU/memory, and host load no container accounts for. |
| `BreachTracker.cs` | One open finding per server and metric: raised once, escalated when worse, closed on real recovery. Shared by all three rules — and, since R1-56, the one duration model of every threshold alert: the container and disk alerts of `MetricsCollectorService` run on it too. `BreachRules` holds the rules (sustained duration, clear margin and duration, escalation step, gap tolerance, minimum gap between two breaches), passed per reading so changed settings apply at the next sample; `BreachRules.Threshold` is the container/disk preset, `GapToleranceFor` the tolerance for a collection interval. A pause longer than the tolerance resets the breach instead of bridging it — and a reported breach it resets is closed then, with a Cleared finding saying the recovery is not confirmed (review round 2: one rule for every reset, so no raise is left open; one transition per reading, a raise due in the same reading follows at the next); a kind outside `NotificationEventTypes.RecoverableKinds` is refused. `Snapshot`/`Restore` (record `BreachState`) keep the reported breaches and the last end of each series across a restart; a restored breach counts its gap tolerance from the restart. |
| `SeriesGapTracker.cs` | R1-56: a series that stops delivering readings is never silent — one `metrics_gap` once its readings are missing for longer than the tolerance (naming the alerts it leaves unconfirmed), one `metrics_gap_recovered` when they are back, and a gap only noticed when the next reading arrives (the loop was held up) is not reported per series — the collector says a held-up cycle once per server with an alert it resets. `Covered` marks a series whose gap a wider one reports (a host's disk). |
| `ApiLatencyEvaluator.cs` | The daemon's own response time — an overloaded Docker, whoever is overloading it. |
| `RollingBaseline.cs` | Deviation from a host's own normal, **and** the guard for when that normal has drifted past the fixed limit. |

## Four rules, and why none of them replaces another

- **Sustained threshold** — the machine is busy. Simple, and it is what the incident needed.
- **Unexplained load** — the machine is busy and no container accounts for it. Names the class of cause.
- **Response time** — the daemon is slow. Catches the *transition*; sees overload the host's own CPU cannot,
  including a link that has gone bad rather than a host.
- **Deviation from normal** — this host is not behaving like itself, whatever the absolute numbers say.

They overlap on purpose. The 2026-08-26 incident would have tripped three of them, and the one thing each
does that the others cannot is written next to it in the code.

## The trap that keeps coming back

Three times now the same shape has appeared in this codebase: a measure that adjusts to the thing it is
supposed to detect.

1. The log-scan watermark that grew with every failed fetch, making the next attempt more expensive.
2. An API-latency baseline that absorbed the slowdown it was written to find (caught during development —
   with the recent readings in the baseline, a 100 ms → 5 s step produced a ratio under two).
3. The rolling baseline itself, which after an hour of plateau decides 98% is normal and goes quiet.

The third one cannot be designed away — a rolling baseline that does not roll is not a baseline. So it is
handled instead: `RollingBaseline` watches **its own mean**, and when the learned normal crosses the fixed
threshold, that is the finding. Four of that class's nine tests are about this one behaviour.

## The gap this closes

Alerts were evaluated per container, plus one server-level rule for disk. `dockerd` runs in no container, so
on 2026-08-26 Whiskers recorded roughly 8,900 measurements of BurgCloud over six days — practically every one
above 98% CPU — and judged none of them. It was noticed because a person happened to look at a dashboard.

## Two CPU numbers, two conventions

This is the work of the package and its main source of error:

- **Host CPU** is a percentage of the **whole machine**. 100 means every core is busy.
- **Container CPU** is Docker's convention, where one fully busy core is 100. A 2-core host can report 200.

During the incident the host read 98.3% while the outside measurement read 195.8 of 200 — the same load, two
scales. `HostSample.ContainerCpuPercentOfMachine` divides the container sum by the core count before anything
is compared.

Getting this wrong fails **silently and in the dangerous direction**: an un-converted container sum is
over-counted, the unexplained gap comes out too small, and the alert never fires. A test on a 4-core host
pins exactly that case, because BurgCloud's own numbers do not expose it — 98 − 24 clears the threshold
either way.

## Driven by sample time, never the wall clock

Every threshold here advances on the timestamp inside the sample. That is what lets `HostLoadReplayTests`
push a whole week through the rules in under a second and still get findings dated 20 August, 14:14 — twelve minutes after the step, where the incident ran for six days. A rule
that consulted `DateTime.UtcNow` could not be replayed — and a rule that cannot be replayed cannot be shown
to catch the incident it was written for.

The replay series is **reconstructed from the incident report's documented values, not recorded**. It proves
the rules fire; it cannot prove they stay quiet through a normal week, because it has none of the texture
that produces false alarms. That second question needs the real series and is still open: a two-week field run measuring the hit and
false-alarm rates.
