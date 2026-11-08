# Services/Observability/Outcomes

Did an automatic action achieve anything? (Plan-0006.) Every action Whiskers takes on its own is recorded together
with a success criterion declared in advance, and judged after its check window. Observing only: nothing acts on
the verdicts yet (automatic rollback and the repeat lock are deliberately not wired up).

| File | What |
|---|---|
| `ActionOutcomeCriterion.cs` | `AutomaticActionKind` (every kind of automatic action) and the criterion an outcome is judged by. |
| `ActionCriteria.cs` | The criteria per action kind and the metric names they read, so a typo cannot create an unevaluatable criterion. |
| `IActionOutcomeService.cs` | The contract and `OutcomeTally` (how often each action kind worked; null when nothing has been judged yet). |
| `ActionOutcomeService.cs` | Records actions and judges due windows. Reads existing series only (self-metrics, stored server metrics); it measures nothing itself. |
| `ActionOutcomeSweeper.cs` | Background loop, once a minute: judges the windows that have come due. Touches no server. |
| `OutcomePresenter.cs` | How much a hit rate can be trusted (`RateTrust`) and how the self-status page phrases it. |
