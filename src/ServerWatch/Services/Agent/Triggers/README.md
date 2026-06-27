# Services/Agent/Triggers

**AI triggers** — run the agent autonomously when an event occurs. A dispatcher taps every
`NotificationEvent` (via [`../../Notifications/CompositeNotificationService.cs`](../../Notifications/CompositeNotificationService.cs))
and, for each enabled matching trigger, starts an agent run under the trigger's chosen guardrail
preset (admin principal, so the preset is the ceiling), auto-approving confirmations up to the
preset level. The result is sent as a notification (`agent_action`) and written to the audit log.

Safety: recursion guard (ignores its own `agent_action` events), per-container cooldown, and a
concurrency cap. Runs only when the agent is enabled.

## Files

| File | Purpose |
|---|---|
| `AiTriggerStore.cs` | `IAiTriggerStore` + `AiTriggerStore` — persists the triggers in `ai-triggers.json` (admin-only). |
| `AiTriggerDispatcher.cs` | `IAiTriggerDispatcher` + `AiTriggerDispatcher` — matches events to triggers and runs the agent autonomously; resolves its dependencies lazily from the root provider to avoid a DI cycle with the notification service. |

## Related

- Model + event catalog: [`../../../Models/AiTrigger.cs`](../../../Models/AiTrigger.cs)
- Guardrail presets that bound each run: [`../Guardrails/`](../Guardrails/)
- Event source / hook: [`../../Notifications/`](../../Notifications/)
- Metric-threshold events (`high_cpu` / `high_memory` / `metric_anomaly`): [`../../Metrics/MetricsCollectorService.cs`](../../Metrics/MetricsCollectorService.cs)
- UI: [`../../../Components/Pages/AiTriggers.razor`](../../../Components/Pages/AiTriggers.razor)
