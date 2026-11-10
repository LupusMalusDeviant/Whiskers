# Services/Observability/SelfMetrics

How Whiskers itself is doing (Plan-0003): per background loop and server, when it last ran, last succeeded, what it
skipped and how long its calls took. Shown on the self-status page.

| File | What |
|---|---|
| `ISelfMetrics.cs` | The contract and `LoopHealth` (one loop against one server). |
| `SelfMetrics.cs` | In-process counters. Lock-free reads of process memory only: collecting them never costs a Docker call, a database round trip or a lock across an await (`SelfMetricsTests` pins that). Labels are loop, server and counter, never a container id. |
| `SelfMetricsFleetExtensions.cs` | One-line shorthands for a loop to report what it did and did not look at, and the shared loop names. |
| `SelfMetricsRecorder.cs` | Background loop: writes the counters to the database once a minute and restores them on boot, so "last success" survives a restart. |
| `SelfStatusPresenter.cs` | The verdict per loop row on the self-status page (`LoopVerdict`). |
| `ActionTimeline.cs` | The timeline of automatic actions and their outcomes (`TimelineEntry`). |
