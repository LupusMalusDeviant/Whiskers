# Contract 0012 — Resolved target objects (INV-57)

Status: **Accepted** · 8 November 2026 by the maintainer, with the decisions D1–D15 as written and every question
Q1–Q7 answered as recommended (see *Answered*). Amendments A1–A4 accepted 10 November 2026, A5–A7 accepted
10 November 2026, A8–A11 accepted 10 November 2026. A12 follows the decision on #187, A13 the decision to bind the connection (both 13 November 2026).

## Purpose

INV-57: *every mutation acts on exactly one resolved target (server + resource) that exists in the inventory; an
ambiguous or unknown target is refused before policy evaluation.* Since R1-06 the gateway **decides** on a resolved
target, but several executions still **act** on something else: an id from the page's own listing, a webhook's stored
name looked up again, a pod's controller derived again at execution, a volume or log-alert rule the gateway never
resolved. This contract makes the decided object the only thing an execution can act on, and proves it mechanically.

Maintainer decision, 8 November 2026 ("Vollständig"): the gateway also resolves volumes and log alerts; every delegate
(UI, automation, webhooks, workloads) receives the resolved target object from the decision and does not look it up
again. Afterwards INV-57 is ✅ without a limitation.

## Current state (verified at `9a12f7a3`)

- `MutationTargetResolver` (`Services/Mutations/MutationTargets.cs:115`) resolves `container`, `host`, `server`,
  `network`, `cloud-server`, `scheduled-task`, `dns-zone`, `workload`, `git-deploy-app`, `webhook`. Everything else falls
  into `Pass` (`:149`) with `Resolved = false`: `volume` (3 capabilities), `log-alert` (3), and the Whiskers-wide kinds
  `config` (13), `account` (3), `identity` (2), `whiskers` (9), `fleet` (4).
- The decided target reaches the delegate only as `MutationContext.Request.Target` (`MutationModels.cs:243`). The two
  runners drop it: `IUiMutationRunner.RunAsync` and `IAutomationMutationRunner.RunAsync` take
  `Func<CancellationToken, Task<T>>` (`UiMutationRunner.cs:24`, `AutomationMutationRunner.cs:205`), so every lambda acts
  on the ids it captured from its caller.
- MCP and agent tools act on `MutationTargets.CanonicalArguments` (`:43`): the resolved id and server written back into
  the arguments. That path already acts on the decided identity.
- The scope guard binds the call to the decision only where an operation marks its object with `[MutationTarget]`
  (16 of 165 `[MutatingOperation]` methods), and then also accepts "an argument value policy saw"
  (`MutationScope.AuthorizesTarget`, `:262`).
- Re-lookups at execution: `WebhookService.TargetAsync` / `RunAsOwnerAsync` (`:1130`, `:1213`) resolve the container
  (`WebhookTargets.ContainerAsync`), the compose plan and the Git app themselves and hand the gateway their own result;
  `KubernetesWorkloadProvider.ScaleAsync` / `RestartAsync` (`:88`, `:143`) read the pod and derive its controller again
  (`ResolveOwner`); `TaskExecutor` `db.backup` looks the container up with `FirstOrDefault` on id **or** name (`:206`);
  `VolumeBackupsView` passes a possibly empty `serverId` that the service reads as `local` (`OmittedServer.Local`) while
  the gateway reads empty as the default server.

## Decisions (D1–D15, recommendation first)

### Model

- **D1 — `ResolvedTarget`, one type per kind.** An abstract record `ResolvedTarget` (`Kind`, `ServerId` — canonical,
  `null` only for Whiskers-wide kinds —, stable `Id`, `Name`, `Aliases`, `Related`, `Fingerprint`) with one sealed
  record per kind: `ResolvedServer`, `ResolvedHostObject` (server + optional named sub-object, D11), `ResolvedContainer`
  (full container id, name, server, compose project), `ResolvedNetwork`, `ResolvedVolume`, `ResolvedVolumeBackup`,
  `ResolvedLogAlertRule`, `ResolvedWorkload`, `ResolvedCloudServer`, `ResolvedScheduledTask`, `ResolvedDnsZone`,
  `ResolvedGitApp` (with `Revision`), `ResolvedWebhook` (with its resolved action target, D7), `ResolvedRecord`
  (Whiskers-wide records, D12) and `ResolvedNew` (an operation that creates its object: only the server and the name it
  will have). `MutationTarget` stays the recorded form (decision record, audit, approval subject); `ResolvedTarget.Record`
  produces it, so INV-22/INV-45 rows are unchanged.
- **D2 — Every capability declares how its target resolves.** `Capability` gains `Resolution`
  (`Object` / `Server` / `Creates` / `Singleton`) and the CLR type of its `ResolvedTarget`. The resolver has a branch for
  every kind; `Pass` remains only for `Creates`. A catalog test refuses a capability without a resolver branch or with a
  type that does not match its kind. Stable identities per kind: container → full container id; network → network id;
  volume → (server, volume name, driver); volume backup → backup id; log alert → rule id; workload → (server, namespace,
  controller kind, controller name, controller UID; the pod id for a bare pod); webhook → webhook id; Git app → app id +
  revision; task → task id; cloud server → Whiskers server id; DNS zone → normalised zone; records → their store id.
- **D3 — Runner signatures carry the object.** Both runners and `TaskExecutor.RunGatedAsync` get
  `RunAsync<TTarget, T>(…, Func<TTarget, CancellationToken, Task<T>> action, …) where TTarget : ResolvedTarget`
  (plus the `Task` variant), still `[MutationEntryPoint]`. The page writes the type in the lambda
  (`(ResolvedContainer c, CancellationToken ct) => Docker.StartContainerAsync(c.Id, c.ServerId)` — C# requires
  either every lambda parameter typed or none; amendment A3); the runner refuses a `TTarget` that is
  not the capability's type before the gateway is asked (recorded via `RefuseAsync`, INV-45). `MutationContext` gains
  `Target`. `ComposeImpact.RunForEachAsync` hands each nested decision its `ResolvedContainer`. The old
  `Func<CancellationToken, …>` overloads stay unmarked during the migration (no `[Obsolete]`, amendment A4; the
  pending list of D10 tracks the remaining callers) and are **deleted** in the last step (no compatibility shim).

### "Cannot obtain a different object"

- **D4 — Three locks.** (a) Only the resolver constructs a `ResolvedTarget`: the constructors take a `ResolverToken`
  whose only instance is private to `MutationTargetResolver` (pages share the assembly, so `internal` is not enough).
  (b) Every `[MutatingOperation]` that acts on one object marks it `[MutationTarget]` (today 16; after this contract every
  object-acting operation, the rest listed with a reason). `AuthorizesTarget` then accepts only the decided `Id` / name /
  aliases on the decided server, and `Related` only for capabilities that compose (`MutationCompositions`) or plan
  (compose); the "argument value policy saw" fallback is removed. (c) The IL scan of D10.
- **D5 — Volumes.** `volume.backup`: the server first (empty = default server, decided once, R1-06), then the volume on
  it from a new read `IVolumeBackupService.ListVolumeInfoAsync(serverId)` (`docker volume ls --format json`: name,
  driver, labels). Exact name only (Docker volume names are unique per host; no prefix). Unknown → `TargetUnknown`
  before policy. `containerName` stays a label of the backup, not a target. `volume.restore` / `volume.backup_delete`:
  the backup record by its id, its stored server resolved (a removed server → refused), `Name` = the volume,
  `Aliases` = file name. A restore into an existing volume adds that volume as `Related`; into a new name it is
  `Creates` for that volume (Q4). The retention delete of a scheduled task decides the exact list of backup ids it will
  remove (like `host.prune`'s named containers) and deletes only those.
- **D6 — Log alerts.** Whiskers-wide (`ServerId = null`). `logalert.toggle` / `logalert.delete`: the rule by `ruleId`
  from `ILogMonitorService.GetRulesAsync`; a call with only a name matches names exactly, more than one →
  `TargetAmbiguous`. `logalert.create` is `Creates`. The rule's container filter is a filter by design (it outlives a
  recreated container) and is recorded, not resolved (Q5).
- **D7 — Webhooks.** A delivery decides on the **stored** webhook, never on its own lookup: the run passes `webhookId`
  and the stored `TargetId`/server, the gateway resolves the action target (container, compose plan, Git app at its
  revision) and the delegate receives a `ResolvedWebhook` whose `Action` target it acts on. The pre-lock read in
  `TargetAsync` stays only to choose the lock key; its result is passed as `MutationRequest.ExpectedTarget` (new,
  optional), and a resolution that differs is refused before policy as `TargetChanged` under the run's `EventKey`
  (one event, INV-45). `WebhookTargets.ContainerAsync` is deleted; `ComposePlanAsync` runs only in the resolver. The
  page's "Test" and the delivery share the same path.
- **D8 — Workloads.** `IWorkloadProvider.StartAsync/StopAsync/RestartAsync` take a `ResolvedWorkload` instead of a pod
  id. The Kubernetes provider patches exactly the decided controller (kind, name, namespace) with its UID as a
  precondition; a bare pod is deleted with the decided pod UID as `DeleteOptions.Preconditions`. No `ReadNamespacedPod`
  / `ResolveOwner` at execution. A UID mismatch (the controller was replaced meanwhile) surfaces as `TargetChanged`
  (D9). The Docker provider acts on the decided container id.

### Between decision and execution

- **D9 — Confirmed before it runs.** For `Object` capabilities the gateway calls
  `IMutationTargetResolver.ConfirmAsync(ResolvedTarget, ct)` after the intent and the operation claim and **before** the
  approval is consumed and the delegate starts — the same place as the approval-lapse refusal (`MutationGateway.cs:645`).
  `ConfirmAsync` reads the object by its stable id and compares the `Fingerprint` (container: id + created; workload:
  controller UID; webhook: hash of action, target, server, owner, enabled; Git app: revision; volume: name + driver;
  backup / rule / record: existence of the id). Vanished → `TargetUnknown`, changed → `TargetChanged`: the intent row
  ends as `Denied`, the claim is released, the approval stays usable, nothing runs — **one** event per attempt
  (INV-45). A read that fails refuses (fail closed). A change *during* the delegate is the operation's own failure (the
  Kubernetes precondition, Docker "no such container"), recorded as the outcome of the same event.

### Coverage and proof

- **D10 — Mechanical test `ResolvedTargetFlowTests`** (IL, next to `MutationCallScanner`, which already maps lambdas
  and async state machines):
  1. In every lambda handed to a `[MutationEntryPoint]` method, and in every helper it inherits its context from, the
     argument passed to a `[MutationTarget]` parameter and to a `serverId` parameter of a `[MutatingOperation]` must
     come from the lambda's `ResolvedTarget` parameter (a property chain on it); a captured local, a field, a literal or
     a method result is a finding.
  2. No call to a method marked `[InventoryLookup]` (new attribute on `ListContainersAsync`, `GetWorkloadAsync`,
     `GetWebhookAsync`, `GetAppAsync`, `GetTasksAsync`, `GetRulesAsync`, `ListBackupsAsync`, `ListVolumeInfoAsync`,
     `CloudServers`, `GetServers`, …) inside such a lambda or helper. Named exceptions with a reason, pinned by the
     test: the compose re-check (`ComposePlan.EnsureDecided`, contract 0006) and verifiers' read-back (R1-37).
  3. Every `[MutatingOperation]` whose capability is `Object` has exactly one `[MutationTarget]` parameter, or is in a
     pinned list with a reason.
  4. Fixtures (negative cases compiled into the test assembly): captured page id, re-lookup in the delegate, literal
     server, `ResolvedTarget` built outside the resolver (does not compile → checked by reflection on the constructors).
- **D11 — Host objects.** nginx sites (`nginx.update`, `site_enable`, `site_disable`), systemd units (`systemd.manage`)
  and a removed firewall rule (`firewall.rule_remove`) are resolved on their server against the host's own listing
  (sites-available, `systemctl list-unit-files`, the numbered rule list); unknown → refused before policy. Host-wide
  operations (`host.exec`, `compose.deploy`, `app.deploy`, `firewall.set_status`, `ssl.renew`, …) keep the server as their
  object. This is wider than the maintainer's sentence and therefore Q1.
- **D12 — Whiskers-wide records.** Kinds `config`, `account`, `identity`, `whiskers`, `fleet` and the `server`-kind
  endpoint checks resolve their record against its store (`ResolvedRecord`): config sections against the closed list of
  sections, accounts by handle, identity bindings, MCP keys, registries, vault secrets, self-backups, endpoint checks by
  id; `Creates` for new ones; `Singleton` for the kill switch, notifications and fleet-wide runs (the object is Whiskers
  itself, `ServerId = null`). Unknown → refused before policy.
- **D13 — MCP and agent unchanged.** They keep acting on `CanonicalArguments`; volumes, log alerts, host objects and
  records gain their canonical keys there. D4 (b) binds their calls too.
- **D14 — No schema change.** The fingerprint lives in memory between resolution and `ConfirmAsync` (one process, one
  call); an approve-then-retry resolves again. Decision record, audit and journal columns stay; the reason codes
  `TargetUnknown`, `TargetAmbiguous`, `TargetChanged` exist (`MutationModels.cs:193–202`), `ExpectedTarget` is a request
  field, not stored. No migration.
- **D15 — Gate.** INV-57 → ✅ with the wording below, its *Open invariants* row in `RELEASE_GATE.md` removed (INV-57
  then has no row at all), `GateParserTests` (which asserts INV-57 as Partial, `:178`) moved to another open invariant,
  and the tests of the next section named in `.github/required-tests/in-process.txt`.

## INV-57, new wording (replaces the evidence column)

✅ — Every capability declares its resolution (object, server, created, singleton) and the gateway resolves every kind
against its inventory before policy: servers, containers, networks, volumes and volume backups, log-alert rules,
Kubernetes workloads (controller and UID), cloud servers, tasks, DNS zones, Git apps, webhooks with their action
target, host objects (nginx sites, systemd units, firewall rules) and Whiskers' own records; unknown or ambiguous is
refused before policy. Every execution receives the resolved object (`ResolvedTarget`) from the decision — UI and
automation through the runners, webhooks and workloads included — and the object is confirmed right before it runs
(vanished or changed → refused, one event). `ResolvedTargetFlowTests` proves no delegate acts on an id of its own or
looks its target up again. Tests: `TargetResolutionTests`, `ResolvedTargetFlowTests`, `TargetConfirmationTests`,
`ToolAuthorizationTests`.

## Affected paths

| Surface | Where | Sites | Change |
|---|---|---|---|
| UI pages | 37 Razor files (`Components/Pages`, `Components/Shared`) | 131 calls of `Mutations.RunAsync` / `RunForEachAsync` (128 with a literal capability, 3 with a variable: `ContainerDatabasePanel`, `ScheduledTasksView`, `WebhooksView`) | lambda takes the typed object (D3) |
| UI helpers | `ComposeImpact` (`RunForEachAsync`, env-file save), `VaultBackedSettingsSave` | 3 helpers, 4 calls | same |
| Automation | `TaskExecutor` (`RunGatedAsync`, 9 capabilities), `AutoUpdateService` 2, `AutoPatchService` 2, `GitDeployOwners` 1, `EndpointCheckRunner` 1, `VpnBootstrapHostedService` 1 | 16 calls | same; `db.backup` loses its own `FirstOrDefault` lookup |
| Webhooks | `WebhookService` delivery (4 actions), page test, `WebhookTargets` | 4 + 2 | D7 |
| Workloads | `IWorkloadProvider` (3 operations), Kubernetes and Docker providers, `Dashboard.razor` (3 calls) | 3 + 2 + 3 | D8 |
| Resolver | new branches: volume, volume backup, log alert, host objects, records | 5 kinds, 45 capabilities | D5, D6, D11, D12 |
| Gateway | `ConfirmAsync`, `ExpectedTarget`, runner type check | — | D9, D7, D3 |
| MCP / agent | `CanonicalArguments` keys for the new kinds | 1 tool (`create_log_alert`) + host-object tools | D13 |

**In total ≈ 176 call sites** (131 + 4 + 16 + 6 + 8 + `create_log_alert` and host-object tools ≈ 11) plus the 165
`[MutatingOperation]` methods that D10 (3) checks for `[MutationTarget]`. The authoritative list is produced by
`ResolvedTargetFlowTests` itself: step 2 adds it as a pinned *pending* list that may only shrink, step 7 requires it
empty.

## Tests (acceptance)

- `TargetResolutionTests` (extended): per new kind — known, unknown, ambiguous, refused before policy (the policy is
  never called); volume on the default server when `serverId` is empty; backup on a removed server; log rule by name
  ambiguous; nginx site / systemd unit unknown; every catalog capability has a resolver branch and the declared type.
- `TargetConfirmationTests` (new): object vanished / changed between decision and run → `Denied` with
  `TargetUnknown` / `TargetChanged`, exactly one audit event, claim released, approval still usable, delegate not
  called; a failing confirmation read refuses.
- `ResolvedTargetFlowTests` (new, D10 items 1–4) with negative fixtures.
- `WebhookMutationTests` (extended): a container renamed / replaced after the lock → `TargetChanged`, one event under the
  run's `EventKey`; the delegate acts on the resolved id; Git app saved meanwhile → refused (existing behaviour kept).
- `KubernetesWorkloadProviderTests` (extended, fake client): the patch names the decided controller and carries its UID;
  no pod read at execution; bare pod delete with UID precondition.
- `UiMutationRunnerTests` / `AutomationMutationRunnerTests`: wrong `TTarget` refused and recorded; the delegate receives
  the object the gateway decided, with the canonical server (also when the caller passed none).
- `MutationBypassArchitectureTests`: entry-point list unchanged in names (new overloads), old overloads gone (step 7).

## Documentation

`SECURITY_INVARIANTS.md` (INV-57 row), `RELEASE_GATE.md`, `PRODUCT_CONTRACT.md` §7 common lines of M and U rows
("containers resolved … other kinds pass through", "container target resolved"), `CAPABILITY_INVENTORY.md` (generated:
new column *Target*), ADR-0005 step 1 (resolution covers every kind), READMEs of `Services/Mutations`,
`Services/Webhooks`, `Services/Workloads`, `Services/Workloads/Kubernetes`, `Services/Backup`, `Services/LogMonitor`,
and the codebase map. Each PR updates the documents of what it changes.

## Work split (PR-sized)

| Step | Content | Triage |
|---|---|---|
| 1 | `ResolvedTarget` types + token, catalog `Resolution`, resolver branches for volume, backup, log alert, records (D1, D2, D5, D6, D12); catalog test | [Claude-prüfbar] |
| 2 | Gateway `ConfirmAsync`, `ExpectedTarget`, `MutationContext.Target`; new runner overloads (old ones unmarked, A4); `ResolvedTargetFlowTests` with the pending list (D3, D9, D10) | [Claude-prüfbar] |
| 3 | Automation: `TaskExecutor`, AutoUpdate, AutoPatch, GitDeployOwners, endpoint checks, VPN | [Claude-prüfbar] |
| 4 | Webhooks (D7) | [Claude-prüfbar] |
| 5 | Workloads (D8) | [Claude-prüfbar]; live check [braucht Nutzer/Hardware: k3s-Cluster] |
| 6 | UI part 1: containers, networks, volumes, databases, workloads, deploy, terminals, host pages (incl. D11 host objects) | [Claude-prüfbar]; host listings live [braucht Nutzer/Hardware] |
| 7 | UI part 2: Whiskers-wide pages (settings, accounts, keys, notifications, triggers, guardrails, schedules, webhooks page); delete the old overloads; `[MutationTarget]` tightening (D4 b); pending list empty; gate and docs flip (D15) | [Claude-prüfbar] |

Steps 3–6 are independent of each other after step 2. The pending list makes every intermediate main state green and
honest: INV-57 stays ⚠️ until step 7.

## Answered (maintainer, 8 November 2026)

Every question was answered as recommended; the answers are decisions of this contract.

- **Q1 — Host objects (D11) are in scope.** nginx sites, systemd units and removed firewall rules are resolved against
  the host's own listing (step 6); INV-57 needs no limitation for them.
- **Q2 — Confirmed before every run (D9), UI included.** For `Object` capabilities `ConfirmAsync` runs before every
  execution, from every surface; skipped for `Server` / `Singleton` / `Creates`.
- **Q3 — Webhook lock key (D7):** the pre-lock read stays and chooses the lock key; its result is compared as
  `ExpectedTarget`, a different resolution is refused as `TargetChanged`.
- **Q4 — Volume restore into a name that does not exist** stays allowed, as `Creates` for that volume (today's
  behaviour).
- **Q5 — The log-alert container filter stays a name**, recorded, not resolved: it is a filter that must outlive a
  recreated container, not a target.
- **Q6 — The old runner overloads are deleted in the last step, without a shim.**
- **Q7 — Kubernetes UID preconditions** are tested with a fake client in CI plus one manual check on a k3s cluster
  (numbered checklist item, [braucht Nutzer/Hardware]) before INV-57 is set to ✅.

## Amendments (accepted by the maintainer, 10 November 2026)

Found while building step 1; each replaces the wording above where they differ.

- **A1 — Upserts are `Creates` with type `ResolvedTarget`.** Capabilities that save an object which may or may not
  exist yet (`server.onboard`, `server.save`, `gitdeploy.save`, `webhook.save`, `vault.secret_set`, `registry.save`,
  `endpointcheck.save`; `CapabilityCatalog.Upserts`) declare `Resolution = Creates` and the abstract `ResolvedTarget` as
  their type. The execution receives the existing object (confirmed before it runs, D9) or a `ResolvedNew`.
- **A2 — New interface `ILocalAccountDirectory`** (`Services/Auth/LocalAccountDirectory.cs`): the inventory an
  `account.*` target resolves against (D12). `FindAsync(email)` returns the stored email of an existing local account or
  `null`, reads in a scope of its own and independent of the caller (a Viewer changing their own password resolves too),
  and throws on a failed read instead of answering "not found" (fail closed). `ILocalAccountService.ListAsync` stays
  Admin-only.
- **A3 — Lambda syntax.** D3's example is written as valid C#: `(ResolvedContainer c, CancellationToken ct) => …`
  (C# allows either every lambda parameter typed or none).
- **A4 — No `[Obsolete]` on the old runner overloads.** Marking them would add a warning for each of the ~150 remaining
  callers to every intermediate build and bury real warnings; the pending list of `ResolvedTargetFlowTests` tracks them instead and step 7 deletes
  the overloads (Q6 unchanged).

## Amendments A5–A7 (accepted by the maintainer, 10 November 2026)

A5 and A6 were found while building step 2; A7 clarifies D7 before step 4. Each replaces the wording above where they
differ.

- **A5 — `ConfirmAsync` signature and how it reads.** D9 names `ConfirmAsync(ResolvedTarget, ct)`. Built as
  `ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target, ct)` returning a
  `TargetResolution`: the confirmation resolves the request **as the caller named it** once more, through the same
  branch as the decision, and compares the object's type, stable id, server and `Fingerprint`. The resolver needs the
  capability and the arguments for that (a Git deployment's per-container decision, a restore's target volume, a
  network's named container); reading the canonical id alone would take another branch for some of them. Outcome as in
  D9: a different id or fingerprint → `TargetChanged`; no object → `TargetUnknown` (or `TargetChanged` when the
  resolver now refuses it as ambiguous or changed); a read that throws → refused as `TargetNotResolved`.
- **A6 — Runner doubles.** The typed overloads are default interface members that throw `NotSupportedException`, so a
  test double of `IUiMutationRunner` / `IAutomationMutationRunner` without a gateway cannot run a typed lambda (it could
  not hand out a `ResolvedTarget` anyway, D4 a); the two real runners implement them.
- **A7 — Webhook actions get the typed target of their capability (clarifies D7).** A delivery is not decided as one
  `ResolvedWebhook` whose `Action` it acts on; each webhook action is decided as the capability it runs, and its
  delegate receives that capability's resolved type: `restart` (`container.restart`) and `rebuild`
  (`container.update`) a `ResolvedContainer`, `deploy` (`compose.deploy`) the `ResolvedHostObject` of its server,
  `git-deploy` (`gitdeploy.deploy`) a `ResolvedGitApp`. The read before the lock stays (it chooses the lock key, Q3)
  and its result is passed as `MutationRequest.ExpectedTarget`; a resolution that differs is refused before policy as
  `TargetChanged`. The delegate acts only on the resolved object (`c.Id` / `c.ServerId`, the app's id and revision,
  the host's server). `WebhookTargets.ContainerAsync` is **not** deleted: it remains the pre-lock read that chooses
  the lock key and the `ExpectedTarget`, and nothing acts on its result. `ResolvedWebhook.Action` stays `null` for
  webhook management capabilities (`webhook.*`).

## Amendments A8–A11 (accepted by the maintainer, 10 November 2026)

A8–A11 were found while building steps 5 and 6. Each replaces the wording above where they differ.

- **A8 — Kubernetes RBAC.** D8 needs, besides the existing rules, `get` on `replicasets` (the decision reads a
  ReplicaSet's owner reference to find the Deployment and its UID) and `patch` on `deployments`, `statefulsets` and
  `daemonsets`. Restart and scale patch the controller itself with a JSON merge patch that carries `metadata.uid` (the
  API server refuses the patch when the UID differs), not its `scale` subresource; a bare pod is deleted with
  `DeleteOptions.Preconditions` naming the decided pod UID. Upgrade note in `CHANGELOG.md` and in the Kubernetes
  documentation and manifest examples (`deploy/k8s/`): a cluster connected with an older or hand-made role must have
  the role applied again, otherwise start, stop and restart fail with 403.
- **A9 — New read `IWorkloadProvider.GetWorkloadIdentityAsync`** (`[InventoryLookup]`): the resolver's read of a
  workload's controller kind, name and UID (and the pod UID for a bare pod). It is used by resolution and
  confirmation only; a delegate never calls it (D10 item 2).
- **A10 — `IFirewallService.RemoveRuleAsync(string serverId, [MutationTarget] string ruleNumber)`.** The rule number
  is passed as a string (was `int`) so that the decided rule's `Id` is the argument `AuthorizesTarget` compares, and it
  is the operation's `[MutationTarget]`. The service still refuses anything that is not a positive integer.
- **A11 — New read `ISystemdService.ListUnitNamesAsync(serverId)`** (`[InventoryLookup]`): the host's
  `systemctl list-unit-files` listing that D11 resolves a systemd unit against. A host whose listing cannot be read
  refuses the unit with a reason (fail closed).

## Amendment A12 (follows the maintainer's decision on #187, option 2, 13 November 2026)

- **A12 — `MutationTarget.HasControlPlaneState`.** `server.onboard` resolves exactly a server of the inventory: by
  its id, then by its id in another case; never the default server or a display name. An empty, unknown or ambiguous
  id is refused, so the decision and the onboarding (which runs on the canonical id) cannot name two different servers.
  The resolver records whether that server already holds mTLS state (`MtlsOnboardingState.Holds`): TLS client material
  in its record (`TcpUseTls`, a client certificate, key or CA path), or anything in the per-host credential store
  (new `IMtlsClientCredentialStore.HoldsState`: false only for a host directory that is empty or positively missing —
  the nearest listable ancestor does not list it and no component on the way is a file; an
  access or I/O error throws, and a damaged or half written identity file counts). Whatever cannot be read counts as
  state (fail closed). The gateway passes it to the rules as `__resolvedControlPlaneState`; a caller's own `__…`
  arguments never reach them. `ProtectedResourceRule` then treats the re-onboarding as a control-plane capability
  (INV-59). The onboarding receives what was decided (`IOnboardingService.OnboardServerAsync(serverId,
  mayReplaceMtlsState, …)`), works only on the server whose id is exactly that id (never a display name or another
  case), and holds the per-host lock from before it looks the server up to its end (replaces "steps 5–9" of contract 0007, amendment
  10), so the server cannot be removed during the run. Under the lock it checks the state again: a run admitted as a
  first onboarding refuses before anything runs when the host holds state by then. No other capability sets or reads
  it.
- **A13 — `MutationTarget.Connection` (follows the maintainer's decision of 13 November 2026, "bind the connection").**
  For `server.onboard` the resolver also records which host the record reaches (`MtlsOnboardingState.ConnectionOf`:
  connection type, SSH and TCP endpoint, socket, Tailscale SSH, Kubernetes context; not names, groups, tags or
  credentials). It is part of the approval binding (`targetConnection`), so an approval given for one host is unusable
  once the record reaches another. The onboarding receives it (`OnboardServerAsync(serverId, mayReplaceMtlsState,
  decidedConnection, …)`) and refuses, under the host lock, at its start and again before the certificate step, when
  the record no longer reaches that host (or none was passed). A save on the Servers page that changes the connection
  holds the same per-host lock (`MtlsOnboardingState.SaveAsync`) and is refused while an onboarding or a certificate
  step holds it; a save that changes only names, groups or tags is not. **Known limit:** the protection follows
  Whiskers' records, not the physical host — a second record for the same host has no mTLS state of its own, so its
  first onboarding needs no approval. Creating that record is an Admin action (`server.save`) and is audited.

## Not in scope

The compose re-check after `up` (contract 0006) and verifiers' read-back stay reads by design (D10 exceptions). Per-host
credentials (INV-58) and Whiskers' self-protection (INV-59) are unchanged.
