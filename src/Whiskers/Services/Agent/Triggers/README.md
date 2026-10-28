# Services/Agent/Triggers

**AI triggers**: run the agent autonomously when an event occurs. A dispatcher taps every
`NotificationEvent` (via [`../../Notifications/CompositeNotificationService.cs`](../../Notifications/CompositeNotificationService.cs))
and, for each enabled matching trigger, starts an agent run under the trigger's chosen guardrail
preset. Any guardrail confirmation is **denied**, never auto-approved (no human is watching an unattended run). The
result is sent as a notification (`agent_action`) and written to the audit log.

**Owner and grant (PC-D6, PC-D7, R1-13).** Every trigger has an owner (`OwnerId`): the Admin who created it, last
changed its write grant, or switched it on after a suspension or while it had no owner (a takeover). The store sets
owner and suspension itself and ignores what the page sends. A trigger may change nothing until an Admin grants it
`WriteCapabilities` (catalog ids) **and** `WriteTargets` (globs on a resolved id or name); triggers saved before R1-13
have neither (their old `MaxLevel` is ignored) and no owner. `AiTriggerAuthority.BeginRunAsync` re-checks the owner
before every run: no owner, an owner who can no longer sign in, or — with a grant — one who no longer holds a granted
capability (level raised in Settings → MCP and key tool lists included) suspends the trigger (`SuspendedReason`,
switched off) and notifies the Admins (`automation_suspended`); an owner not verified yet is refused without
suspension. A run without a grant gets a `read` principal; with one, the owner's current level — never a synthetic
admin. As the gateway's `ITriggerAuthority` it then checks **every** mutation made for the run — nested ones too (a
scheduled task the trigger runs) — against the current grant, including everything the operation also touches
(`MutationTarget.Related`: the containers a network removal cuts off, the container it connects or disconnects, a
workload's pod), and the owner's current authority: anything outside the grant is refused (`TriggerNotAllowed`), a
lost authority suspends the trigger. The gateway refuses every Confirm of the run and of anything nested in it
instead of raising an approval (`UnattendedConfirm`), and every audit row of the run carries `trigger:<id>`.

**Untrusted event text (INV-27).** The run's first message carries the event — names, image, server, log line — as
one JSON value between `<event-data id="…">` markers with a random id per run, so it can neither end the block nor
read as an instruction; only the Admin's prompt stands outside as the task. Tool results reach the model only as
tool messages. None of it can widen the grant: the gateway enforces it.

**Pinned preset (INV-23, R1-12).** The trigger's preset is copied when the run starts (`PinnedPolicy` on the
`AgentContext`); the session and the Mutation Gateway decide every call of the run against it, so switching or
editing the active preset does not reach a running trigger — only the global kill switch does, and a fail-closed
guardrail store stops it. Mutations nested in a call of the run (a scheduled task it runs) inherit the pin through
the gateway's `MutationScope`. A trigger whose preset does not exist (renamed or deleted) **does not run**: there is no
fallback to another preset; the refusal is audited (`agent.run`, unsuccessful) and the AI triggers page marks the
missing preset.

Safety: recursion guard (ignores its own `agent_action` events), per-container cooldown, and a
concurrency cap. Runs only when the agent is enabled.

## Files

| File | Purpose |
|---|---|
| `AiTriggerStore.cs` | `IAiTriggerStore` + `AiTriggerStore`, persists the triggers in `ai-triggers.json` (admin-only). |
| `AiTriggerDispatcher.cs` | `IAiTriggerDispatcher` + `AiTriggerDispatcher`, matches events to triggers and runs the agent autonomously with the event as delimited data; resolves its dependencies lazily from the root provider to avoid a DI cycle with the notification service. |
| `AiTriggerAuthority.cs` | `IAiTriggerAuthority` + `AiTriggerAuthority`: the level a run starts with, and the gateway's per-mutation check of grant and owner (`ITriggerAuthority`); suspends a trigger whose owner lost the right. |

## Related

- Model + event catalog: [`../../../Models/AiTrigger.cs`](../../../Models/AiTrigger.cs)
- Guardrail presets that bound each run: [`../Guardrails/`](../Guardrails/)
- Event source / hook: [`../../Notifications/`](../../Notifications/)
- Metric-threshold events (`high_cpu` / `high_memory` / `metric_anomaly`): [`../../Metrics/MetricsCollectorService.cs`](../../Metrics/MetricsCollectorService.cs)
- UI: [`../../../Components/Pages/AiTriggers.razor`](../../../Components/Pages/AiTriggers.razor)
