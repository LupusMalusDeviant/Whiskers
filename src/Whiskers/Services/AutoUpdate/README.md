# Services/AutoUpdate

Scheduled **automatic container updates**. When an image update is detected ([`../ImageUpdate/`](../ImageUpdate/)), this service can pull and recreate the container according to policy, and keeps a history of what was updated for the UI.

## Files

| File | Purpose |
|---|---|
| `IAutoUpdateService.cs` / `AutoUpdateService.cs` | Background auto-update of container images; exposes the policy and update history for the UI. R1-31 (INV-53): the rollback snapshot (full configuration, environment included) is stored encrypted with a vault-derived key bound to its container and server. Without a usable vault none is kept and an older one for that container is removed: `CaptureSnapshotAsync` throws `RollbackSnapshotUnavailableException`, the update goes ahead and the callers say it cannot be rolled back. A snapshot that cannot be opened is not rolled back to. R1-36 (INV-44): with the policy's `AutoRollback` on (the default), an update whose new container is not verified or is unhealthy afterwards is rolled back (a recreate that threw restored the old container itself and is not) through the gateway (`container.rollback` on the AutoUpdate owner, verified) and the history row says whether it worked (`RolledBack`) — the snapshot is consumed only once the rollback is verified (the Dashboard's manual rollback calls `CompleteRollbackAsync` after the gated call succeeded); only a state read back as bad rolls back (`UpdateUnhealthyException`, or a verification failure that is not `StateUnknown`) — a read that could not be made is reported, and a rolled-back image digest is skipped until a newer one appears; with it on, a snapshot that failed for another reason than a missing vault stops the update (`RollbackSnapshotRequiredException`). R1-39: each container update is registered with `IShutdownDrain`; once stopping began no further container is updated (its policy is checked again after the restart), and a recreate that runs is waited for within the drain budget instead of being abandoned. The attempt is saved in `UpdateHistory` without an end (`InProgressResult`) before the gated action runs; a container with such an open attempt is not updated again, and `InterruptedWorkReconciler` marks one of an earlier process interrupted and switches its policy off. The loop waits for the start-up reconciliation (`IStartupReconciliation`) before its first check, so it never acts on a policy the pass is about to switch off. R1-41: a failed check is a failed cycle, not the end of the loop. The cycle is owned (no `WaitAsync` abandoning it once the drain budget is spent): the stop reaches the policy query, the fleet listing and the notice before an update, so no update starts after it; an update whose recreate already ran is finished and recorded rather than left running unobserved, within the host's shutdown timeout (past it, the next start reports it as interrupted). A stop that lands after the recreate returned verified, during the health check, records the update as done with its health not checked (not as a failure, no rollback). Follow-up (2026-11-03): the failure notification carries the cycle's token — a stop ends its retries (the composite keeps it for redelivery) and the attempt is still recorded. |

## Behaviour notes

- **Through the Mutation Gateway:** each recreate runs as `container.update` on the authority of the AutoUpdate
  owner (Settings → Automation owners), re-checked per update. Without an owner it is refused (`Automation:RequireOwner`, default `true` since R1-04); the
  grace period applies only when that is set to `false` (ADR-0005 step 2.3, see [`../Mutations/`](../Mutations/)).
- **Suspension:** when the owner lost their authority (account deleted, whitelist/role/key revoked, demoted) AutoUpdate
  is suspended in the owner store and an `automation_suspended` notification goes out; it stays off until an admin
  takes it over. An update refused for a blocked owner entry is recorded as the gateway's Denied `container.update`
  event (INV-45): on behalf of the owner a suspended entry still names, else at read level as unowned.
- **Server-scoped matching:** a policy applies only to its own `ServerId` — a same-named container on another host is never auto-recreated (id-match before name-match; an empty policy `ServerId` matches any host for back-compat).

## Related

- Update detection: [`../ImageUpdate/`](../ImageUpdate/)
- Container recreate: [`../Docker/`](../Docker/)
- Notifications: [`../Notifications/`](../Notifications/)
