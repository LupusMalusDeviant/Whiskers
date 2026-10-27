# Services/AutoUpdate

Scheduled **automatic container updates**. When an image update is detected ([`../ImageUpdate/`](../ImageUpdate/)), this service can pull and recreate the container according to policy, and keeps a history of what was updated for the UI.

## Files

| File | Purpose |
|---|---|
| `IAutoUpdateService.cs` / `AutoUpdateService.cs` | Background auto-update of container images; exposes the policy and update history for the UI. |

## Behaviour notes

- **Through the Mutation Gateway:** each recreate runs as `container.update` on the authority of the AutoUpdate
  owner (Settings → Automation owners), re-checked per update. Without an owner it is refused (`Automation:RequireOwner`, default `true` since R1-04); the
  grace period applies only when that is set to `false` (ADR-0005 step 2.3, see [`../Mutations/`](../Mutations/)).
- **Suspension:** when the owner lost their authority (account deleted, whitelist/role/key revoked, demoted) AutoUpdate
  is suspended in the owner store and an `automation_suspended` notification goes out; it stays off until an admin
  takes it over.
- **Server-scoped matching:** a policy applies only to its own `ServerId` — a same-named container on another host is never auto-recreated (id-match before name-match; an empty policy `ServerId` matches any host for back-compat).

## Related

- Update detection: [`../ImageUpdate/`](../ImageUpdate/)
- Container recreate: [`../Docker/`](../Docker/)
- Notifications: [`../Notifications/`](../Notifications/)
