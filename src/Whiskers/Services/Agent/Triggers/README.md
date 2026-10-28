# Services/Agent/Triggers

**AI triggers**: run the agent autonomously when an event occurs. A dispatcher taps every
`NotificationEvent` (via [`../../Notifications/CompositeNotificationService.cs`](../../Notifications/CompositeNotificationService.cs))
and, for each enabled matching trigger, starts an agent run under the trigger's chosen guardrail
preset. The run's principal is capped at the trigger's configured `MaxLevel` (default **write** —
never a synthetic admin) so the `PrincipalCeilingRule` bounds it, and any guardrail confirmation is
**denied**, never auto-approved (no human is watching an unattended run). The result is sent as a
notification (`agent_action`) and written to the audit log.

**Pinned preset (INV-23, R1-12).** The trigger's preset is copied when the run starts (`PinnedPolicy` on the
`AgentContext`); the session and the Mutation Gateway decide every call of the run against it, so switching or
editing the active preset does not reach a running trigger — only the global kill switch does, and a fail-closed
guardrail store stops it. A trigger whose preset does not exist (renamed or deleted) **does not run**: there is no
fallback to another preset; the refusal is audited (`agent.run`, unsuccessful) and the AI triggers page marks the
missing preset.

Safety: recursion guard (ignores its own `agent_action` events), per-container cooldown, and a
concurrency cap. Runs only when the agent is enabled.

## Files

| File | Purpose |
|---|---|
| `AiTriggerStore.cs` | `IAiTriggerStore` + `AiTriggerStore`, persists the triggers in `ai-triggers.json` (admin-only). |
| `AiTriggerDispatcher.cs` | `IAiTriggerDispatcher` + `AiTriggerDispatcher`, matches events to triggers and runs the agent autonomously; resolves its dependencies lazily from the root provider to avoid a DI cycle with the notification service. |

## Related

- Model + event catalog: [`../../../Models/AiTrigger.cs`](../../../Models/AiTrigger.cs)
- Guardrail presets that bound each run: [`../Guardrails/`](../Guardrails/)
- Event source / hook: [`../../Notifications/`](../../Notifications/)
- Metric-threshold events (`high_cpu` / `high_memory` / `metric_anomaly`): [`../../Metrics/MetricsCollectorService.cs`](../../Metrics/MetricsCollectorService.cs)
- UI: [`../../../Components/Pages/AiTriggers.razor`](../../../Components/Pages/AiTriggers.razor)
