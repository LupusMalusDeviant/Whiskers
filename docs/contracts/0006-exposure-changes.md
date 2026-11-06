# Contract 0006 — Exposure changes in compose deployments

Status: **Accepted** · 6 November 2026 by the maintainer, with the decisions D1–D6 at the end as recommended.
**Amendments 1–6** (review rounds 1–6, at the end) are to be accepted by the
maintainer as one set.

## Purpose

A compose deployment can make a container reachable from outside its host in ways the policy does not see today:

- published ports;
- `network_mode: host`, `privileged`, added capabilities;
- **labels that a tool on the host reads and turns into a public route.** Examples are DockTail's
  `docktail.funnel.*`, which serves the container on the internet through Tailscale Funnel, Traefik routers and
  caddy-docker-proxy. None of these needs a single `ports:` entry.

Each deployment path should name such a change, so that it is never decided without being seen. On the agent's path it
must be confirmed (DockTail evaluation, 6 November 2026, H1).

## Current state (verified at `25797a37`)

- **One plan for every path.** `ComposePlan` / `IComposePlanner` plans every compose deployment: UI editor and App Store
  (`ComposeImpact.DeployAsync`, `GateFor(IUiMutationRunner)`), MCP `deploy_compose` (`ContainerTools.cs:460ff`),
  webhook (`WebhookService.cs:826`, `GateFor(IAutomationMutationRunner)`) and git deploy (`GitDeployOwners.cs:51`).
- **What the plan analyses.** It knows the services and their mounts (R1-16, `ComposeServicePlan.Mounts`). It interpolates
  from the `.env`, and it refuses a value it cannot determine. Its impact covers existing containers (R1-07). It does not
  look at ports, labels, `network_mode`, `privileged` or `cap_add`.
- **How mounts are decided.** They get a decision of their own: a nested decision under `compose.deploy` with the argument
  `mounts` (`ComposeImpact.RunMountsAsync`, `ComposeImpact.cs:63-70` and the automation variant). `ProtectedResourceRule`
  reads them.
- **Which rules apply.** `compose.deploy` requires Admin and is risk Root (`CapabilityCatalog.cs:103`). The agent's or an API
  key's call is evaluated by `GuardrailEngine.CreateDefault()`; under the standard policy (`MaxAutonomousLevel = "read"`)
  that means Confirm, and the starter presets refuse it outright. A person in the UI and an automation owned by a person
  are evaluated by `CreateForHumans()` (principal ceiling, kill switch, protected resources). There, forbidden-argument
  patterns and confirmation do not apply (PC-D1, `MutationPolicy.cs:79-85`).
- **No built-in rule for exposure changes** (`GuardrailEngine.cs:17-26`). The only lever is `ForbiddenArgPatterns`, and
  only on the agent's path, where the YAML is the `composeContent` argument.
- **`app.deploy`** (single container, `ComposeFileParser`) parses ports and `network_mode`, but has no labels. It is out
  of scope here (see D6).

## Boundaries (fixed)

- No change to the auth middleware, the OAuth whitelist, mTLS/WireGuard or the socket-proxy verbs.
- PC-D1 stays: a person acting in the UI is not asked to confirm their own action.
- The plan stays the single source. A deployment whose exposure differs between the decision and the run is stopped,
  like a container that was not decided (`EnsureDecided`).
- A value the plan cannot interpolate stays a refusal, as today. Labels are no way around it.

## Types and interfaces

### Plan

```csharp
public enum ExposureKind { PublishedPort, HostNetwork, Privileged, AddedCapability, HostNamespace, RouteLabel }

/// <summary>One way a planned service is reachable or powerful beyond its own network namespace.</summary>
public sealed record ComposeExposure(string Service, ExposureKind Kind, string Detail);
//  Detail examples: "0.0.0.0:8080->80/tcp", "network_mode: host", "cap_add: NET_ADMIN", "pid: host",
//  "docktail.funnel.enable=true"

public sealed record ComposeServicePlan(string Name, string? ContainerName)
{
    public IReadOnlyList<ComposeMount> Mounts { get; init; } = [];
    public IReadOnlyList<ComposeExposure> Exposures { get; init; } = [];   // new
}

// ComposePlan
public IReadOnlyList<ComposeExposure> Exposures();                          // new, all services, stable order
```

**Detection after interpolation:**

| Kind | What counts | Exception |
|---|---|---|
| `PublishedPort` | every `ports:` entry, short and long form | bound to `127.0.0.1` / `::1` |
| `HostNetwork` | `network_mode: host`; `network_mode: service:x` of a host-network service | — |
| `Privileged` | `privileged: true` | — |
| `AddedCapability` | `cap_add` | — |
| `HostNamespace` | `pid: host`, `ipc: host`, `userns_mode: host` | — |
| `RouteLabel` | a label whose key starts with an entry of `GuardrailPolicy.ExposureLabelPrefixes` | — |

`RouteLabel` reads labels in both the list form and the map form. The default list for `ExposureLabelPrefixes` is
`docktail.funnel.`, `docktail.service.`, `traefik.http.routers.`, `traefik.tcp.routers.`, `caddy`.

### Decision

- `ComposeImpact` gets `RunExposuresAsync` (UI) and an automation counterpart, in the same pattern as `RunMountsAsync`.
  The plan has exposures, so there is a nested decision under `compose.deploy` with the arguments
  `{ serverId, exposure = "<service>: <detail>\n…", exposureClasses = "public,host" }`.
- The classes are:
  - `public`: `RouteLabel` with a Funnel prefix (`docktail.funnel.`), `traefik.*`, `caddy*`;
  - `host`: `HostNetwork`, `Privileged`, `AddedCapability`, `HostNamespace`;
  - `published`: `PublishedPort`, and `RouteLabel` with `docktail.service.` (tailnet only).
- New rule `ExposureChangeRule : IGuardrailRule` (`Id = "exposure-change"`), in `CreateDefault()` only (agent and API
  key):
  - Confirm for the classes in `GuardrailPolicy.ExposureConfirmClasses`. The default is `public`, `host`.
  - The reason names the service and the detail, for example "web-1 becomes public through Tailscale Funnel
    (docktail.funnel.enable=true)".
  - The approval card shows it like other reasons.
- Persons and automations owned by persons:
  - no new rule (PC-D1);
  - the nested decision is audited with the full exposure;
  - when the class `public` or `host` is present, a notification of type `exposure_change` is sent (one per
    deployment, suppressible like other info events).

### Configuration (`GuardrailPolicy`, additive)

```csharp
public List<string> ExposureLabelPrefixes { get; set; } = [ "docktail.funnel.", "docktail.service.",
    "traefik.http.routers.", "traefik.tcp.routers.", "caddy" ];
public List<string> ExposureConfirmClasses { get; set; } = [ "public", "host" ];
```

`Clone()` and the presets copy both. An empty `ExposureConfirmClasses` turns the confirmation off, for an explicit
operator choice.

### Readiness (synergy with H2)

- A new check: with `Auth:Disabled`, the readiness reads Whiskers' own container on its own host. It finds the container
  through the names in `WHISKERS_SELF_CONTAINERS`, as `LogScanExclusions` does.
- If that container carries a label with an exposure prefix, the check is not OK. The text says: "Whiskers itself is
  published through `<label>` with sign-in turned off".

## Tests (acceptance)

- `ComposeExposureTests`:
  - each kind, in the short and long forms;
  - the loopback exception;
  - interpolated labels and ports;
  - labels in list form and in map form;
  - `network_mode: service:x`;
  - a value that cannot be interpolated is refused.
- `ExposureChangeRuleTests`:
  - Confirm per class;
  - an empty list means no confirmation;
  - the reason names the service and the label;
  - not part of `CreateForHumans()`.
- `ComposeImpactExposureTests`:
  - UI, MCP, webhook and git deploy each produce the nested decision with the same arguments;
  - exposure that changes between decision and run stops the deployment;
  - a human-owned webhook deployment with Funnel runs, is audited and notifies;
  - an API-key-owned one with Funnel is refused (Confirm without a person).
- `ProductionReadinessServiceTests`: the own container with `docktail.funnel.enable=true` plus `Auth:Disabled` is not
  OK; without the label, or with sign-in on, there is no line.
- Upgrade: `GuardrailPolicy` without the new fields loads the defaults (the JSON round-trip test).

## Documentation

- `docs/v1/SECURITY_INVARIANTS.md`: a new invariant, "an exposure change is decided as such".
- The guardrails README.
- The README section on deployments, with an upgrade note.
- CHANGELOG (behaviour change: an agent's deployment that publishes through Funnel or the host network now waits for
  confirmation even under a policy that allows Admin autonomously).

## Plan triage

- [Claude-prüfbar] Plan detection, rule, nested decision, notification, readiness check, all tests, docs.
- [braucht Nutzer/Hardware] A real DockTail on a host: does Funnel really appear for a container deployed through
  Whiskers, and does the confirmation stop it?

## Decisions for the maintainer

- **D1: verdict for agent and API key on `public` / `host`.** Recommended: Confirm. Alternative: Deny.
- **D2: persons and human-owned automations.** Recommended: no block (PC-D1), plus an audit entry and an `exposure_change`
  notification. Alternative: Confirm, which refuses unattended webhook and git deploys with Funnel.
- **D3: published ports.** Recommended: only shown in the decision (class `published`), no confirmation by default.
  Otherwise nearly every app deployment would need confirming.
- **D4: label prefix defaults** as listed above. `docktail.service.` (tailnet only) counts as `published`, not `public`.
- **D5: readiness check for the own container** in this package, or split out.
- **D6: `app.deploy`** (single container) is out of scope. It has no labels; its ports and `network_mode` could follow
  later through the same rule.

## Amendment 1 — review round 1 (6 November 2026)

This amendment is binding where it differs from the text above. Decisions D1–D6 are unchanged, and D6 is made more
precise below.

### Agent path through the resolved target (review B1)

- MCP `deploy_compose` makes no nested decision. Its plan is built while the gateway resolves the target
  (`MutationTargets.HostAsync` / `WithPlanAsync`).
- The exposures go there as a new field `MutationTarget.Exposures` (classes and details), and they are part of the
  `ApprovalBinding`.
- `ExposureChangeRule` reads **only** the resolved target, never caller arguments.
- The nested `RunExposuresAsync` decision stays for the UI and automation paths.
- When the tool plans again, `EnsureDecided` must find every exposure in the resolved target.
- Tests:
  - with `MaxAutonomousLevel = "admin"`, Funnel over MCP leads to Confirm;
  - an approval is not reused after a change to `.env` alters the exposure.

### Paths (review M1)

- **In scope:** `container.env_set` (MCP `set_container_env`, the `.env` save on the container page, and
  `MutationTargets` for `container.env_set`), and the App Store (`app.deploy` through `ComposeImpact.DeployAsync`).
- **Out of scope and named as such:**
  - the Deploy page's compose mode (`DeploymentService.DeployFromComposeAsync` / `ComposeFileParser`), which has no
    `ComposePlan`;
  - the `DeploymentRequest` paths (the Deploy form and MCP `deploy_app`).

  The README names these gaps.

### How exposures are detected (review M2)

- **Source.** The union of the plan model and the server-side `docker compose config --format json`
  (`ComposeConfigEffects`), the same way as for mounts. Compose resolves merging, `extends`, `label_file`, `!reset` and
  `!override` itself. A service that only compose knows about is refused.
- **Additional rows in the class `host`:**
  - a service network that refers to an external network named `host`;
  - `uts: host`, `cgroup: host`, `devices:`, `security_opt` with `seccomp:unconfined` or `apparmor:unconfined`.
- **`network_mode: container:<x>`.** It is resolved through the inventory, and x's exposures are inherited. An x that
  cannot be resolved is refused.
- **Label keys.** They are matched case-insensitively. An added prefix that is not built in belongs to the class
  `public`, failing closed.
- **Remaining risk.** Traefik with `exposedByDefault` (`traefik.enable`, `traefik.http.services.*`) is named in the
  README. D4 is unchanged.

### Checked again after `up` (review M3)

- The read-back verifier of `compose.deploy` / `app.deploy` (R1-37) inspects the containers that were created:
  `Config.Labels` and `HostConfig.{NetworkMode, Privileged, CapAdd, PidMode, IpcMode, PortBindings}`. This catches labels
  that come from the image.
- A difference from what was decided means: the operation is reported as failed, there is an `exposure_change`
  notification and an audit entry, and **the container is stopped** (fail closed).
- Test: a label that comes from the image.

### No values in the clear (review M4)

- **`Detail` for a `RouteLabel`.** It holds the key, plus the value only when the value is literal. A value that comes
  from a variable is marked `FromVariable` and goes into `Withheld`, like the mounts.
- **The notification** carries only the service, the kind and the key.
- Test: no `.env` value appears in the audit or in the notification.

### Configuration (review M5)

- `ExposureLabelPrefixes` and `ExposureConfirmClasses` become `OptionalPolicyFields` in `GuardrailStore`, so existing
  preset files remain complete.
- `GuardrailsView.ClonePolicy` copies both fields, and a test pins this.
- `null` means the default.
- The CHANGELOG says that the new defaults change every policy's `VersionHash`. Open approvals and the snapshots of
  pinned runs therefore expire at the upgrade.

### Minors

- **Variables that cannot be resolved.** If a label key, or a value under a matching prefix, or a port, network or
  privilege field cannot be resolved, the deployment is refused. An unresolved value under a key that does not match is
  ignored.
  - The CHANGELOG names this behaviour change, because compose also interpolates from the host's environment.
- **Readiness.** If Whiskers' own container cannot be found or read, the line says "unknown", not OK. The line quotes
  only the label key.
- **Tests for the API key.** They run with `MaxAutonomousLevel = "admin"`. Under the standard policy, `compose.deploy`
  already needs confirmation anyway.
- **`exposure_change`.** It is classified in `NotificationEventTypes.SuppressibleEvents`. A failed send does not undo a
  deployment that has already run.
- **Line references (as of `25797a37`):**
  - `MutationPolicy.cs:74-92`;
  - `ComposeImpact.cs:65-71`;
  - `ContainerTools.cs:450-495`.

## Amendment 2 — review round 2 (6 November 2026)

### Re-check after `up` (review MA-1, MI-2, MI-4 — replaces "Checked again after `up`" in Amendment 1)

- **Where it runs.** Inside the scope of the operation, directly after `up`, on every path that runs a `ComposePlan`:
  - `ComposeImpact.DeployAsync`;
  - webhook `DeployCompose`;
  - `GitDeployService` (`gitdeploy.deploy`);
  - `set_container_env` (`container.env_set`);
  - the `.env` save on the container page (`container.env_file_save`).

  It also runs when `up` failed.
- **Project name.** The check uses the plan's real project name, which is held in memory only.
- **Fail closed.** A run that cannot be checked counts as different.
- **Compared fields.** Exactly the detection rows: `Config.Labels`, and `HostConfig` `NetworkMode`, `Privileged`, `CapAdd`,
  `PidMode`, `IpcMode`, `UTSMode`, `UsernsMode`, `CgroupnsMode`, `Devices`, `SecurityOpt` and `PortBindings`.
- **Comparison rule.** Runtime ⊆ decided, after normalising: the `CAP_` prefix, `container:<id>` versus `service:x`,
  and ephemeral host ports. A runtime set that is smaller than decided is fine.
- **Image labels before `up`.** For services with `image:`, the image is pulled first and its labels are checked with
  `docker image inspect`. `DeployScript` is split for this. Services with `build:` keep a window between start and
  re-check; the README names it.

### Stopping only for the agent rule set (review MA-3, MI-5)

- **When the container is stopped.** Only when the decision came from the agent rule set (`CreateDefault`) and the
  undecided exposure is in a class of `ExposureConfirmClasses`.
  - It is stopped with `docker update --restart=no` and then `docker stop`.
  - Compositions `compose.deploy`, `app.deploy`, `gitdeploy.deploy`, `container.env_set` and `container.env_file_save` →
    `container.stop` are registered for this.
- **Otherwise** (persons and human-owned automations, PC-D1): the operation is reported as failed, audited and notified,
  and the container keeps running.
- **Notification type.** The stop is reported as `exposure_violation`, which is not suppressible. `exposure_change` stays
  suppressible.

### Input to the rule (review MA-2)

- **Where `MutationTarget.Exposures` comes from.** `HostAsync` fills it from the `composeContent` plan. Without
  `composeContent`, it comes from the nested decision's `exposure` argument, as with `mounts`.
- **How the rule gets the exposures.** `WithResolvedTarget` passes them as `__resolvedExposure{i}` keys. Withheld ones are
  passed under a withheld prefix. Keys starting with `__` sent by a caller are stripped, as before.
- **What the target holds.** Kind, service and label key. The rule classifies them with `context.Policy`, so a pinned
  preset uses its own prefixes. `exposureClasses` is informational only.

### Smaller points (review MI-1, MI-3, MI-6, MI-7)

- **Capabilities.** Wherever this contract says `container.env_set`, `container.env_file_save` is meant as well.
- **Variable keys.** A label key that comes from a variable is shown as `label (withheld)`. The reason then falls back to
  kind and class. An entry that only compose reports, and that is marked `FromVariable`, is treated the same way.
- **Remaining risks** named in the README:
  - Traefik `exposedByDefault`;
  - Serve or Funnel through a Tailscale sidecar's serve configuration;
  - `cloudflared` tunnels.
- **Additional tests:**
  - every new detection row: external network `host`, uts, cgroup, devices, security_opt;
  - `container:x` inherits x's exposures, and an x that cannot be resolved is refused;
  - case-insensitive keys, and an operator-added prefix is `public`;
  - a service that only compose knows about is refused;
  - `env_set` and `env_file_save` produce the decision;
  - the re-check runs on every path, git deploy included;
  - the readiness line says "unknown" when the own container cannot be read.

## Amendment 3 — review round 3 (6 November 2026)

Amendment 2 is binding where it differs from Amendment 1 or the original text. Amendment 3 is binding over all of them.
All amendments are to be accepted together.

### Paths (review M-1, M-2)

- The re-check from Amendment 2 also runs on MCP `deploy_compose` (`ContainerTools.DeployCompose`). That tool runs
  `plan.DeployScript` itself.
- `WithPlanAsync` fills `MutationTarget.Exposures` for every plan it builds: `compose.deploy` with `composeContent`, and
  `container.env_set` (resolved in `ContainerAsync`).
- Test: the agent's `set_container_env`, with a Funnel label that comes from a variable, ends in Confirm.

### Target and binding (review M-3, M-4, m-6)

- **What the target holds.** The normalised `Detail` of every exposure. An entry that comes from a variable is held in
  full and marked `Withheld`: it is compared and bound, but never written anywhere, as with the mounts
  (`MutationTargets.cs:326-328`).
- **Nested arguments in two parts:**
  - `exposure` holds only literal entries and is audited;
  - `exposureWithheld` holds the entries that come from variables. `SinkRedaction.IsPayload` covers it, so the sinks keep
    only its length. `HostAsync` marks these entries `Withheld`.
- **Classification.** The rule classifies by the real key, so a withheld key is classified correctly too. Display only
  ever shows `label (withheld)`.
- **Matchers skip exposures.** `ProtectedResourceMatcher`, `ControlPlaneResources` and `ForbiddenArgumentRule` skip
  `__resolvedExposure*`. Withheld exposures use their own prefix, `__resolvedExposureWithheld*`.
- Test: a key from `.env` appears neither in the audit nor on the approval card.

### Comparison in the re-check (review M-5, m-8)

- **How.** The detection table (including the rows added in Amendment 1) is applied to the inspected containers. The
  comparison is on `(service, kind, normalised detail)`. Loopback bindings and labels outside `ExposureLabelPrefixes` are
  dropped beforehand.
- **Image label hit before `up`.**
  - Agent rule set: the deployment is refused before it runs.
  - Persons: the deployment continues, with an audit entry and a notification.
- **Run that cannot be checked.** Every container of the project that this run created or recreated counts as
  undecided.
- **Git deploy.** Built images are inspected between `build` and `up`.
- **Inspected locally.** "Pulled first" means: inspected locally, pulled only if the image is missing. `env_set` and
  `env_file_save` keep `pull: false`.

### Stopping (review M-6, m-7)

- **Primitive.** A new marked operation `container.disable_restart` (`docker update --restart=no`) on `IDockerService`,
  followed by `container.stop`.
- **Compositions.** `compose.deploy`, `gitdeploy.deploy` and `container.env_set` → `container.disable_restart`,
  `container.stop`. The UI-only `app.deploy` and `container.env_file_save` are dropped from this list, because they are
  decided by `CreateForHumans()`.
- **Engine flag.** `MutationPolicyDecision` carries `AgentRuleSet` (true when `CreateDefault` decided). The re-check
  reads it.
- **Failed stop.** It is retried once, then reported as `exposure_violation` with "container still running".
- **Notification type.** A violation is always `exposure_violation` (not suppressible), even when the container keeps
  running.
- **Boundaries.** The boundary "stopped" in the boundary section means: refused before the run. After `up`, Amendment 2
  applies.

### Shared namespaces (review M-7)

- `pid`, `ipc` and `network_mode` in the forms `container:<x>` and `service:<x>` are resolved transitively, to a depth of
  at most 5, and inherit x's exposures. An x that cannot be resolved is refused.
- Test: `pid: container:node-exporter` (`pid: host`) ends in the class `host`.

### Tests (review m-11)

Additional tests:

- stopping under the agent rule set versus continuing to run for persons;
- `exposure_violation` is classified as not suppressible;
- the re-check on MCP `deploy_compose`;
- normalisation (`CAP_`, `container:` / `service:`, ephemeral ports, loopback);
- a `__resolvedExposure*` key sent by a caller is stripped;
- a pinned preset classifies with its own prefixes;
- an image label before `up`.

## Amendment 4 — review round 4 (6 November 2026)

This amendment takes precedence over Amendments 1–3. All amendments are to be accepted together.

### No `.env` values in sinks and reasons (review 0006-A)

- **Payload lists.** `exposureWithheld` is added to `SinkRedaction.PayloadNames` and
  `ProtectedResourceMatcher.PayloadArguments`.
- **Rules skip the nested arguments.** `ProtectedResourceMatcher`, `ControlPlaneResources` and `ForbiddenArgumentRule`
  skip the nested arguments `exposure` and `exposureWithheld`. The resolved target already carries them.
- **Tests:**
  - `IsPayload("exposureWithheld")` is true;
  - a hit on it is never quoted.

### Image labels (review 0006-B)

- **Agent path: inspected while planning.** The local images (`docker image inspect`, no pull) are inspected while the
  plan is built. Their labels enter `MutationTarget.Exposures`, so D1's Confirm works.
- **Image missing locally at planning time.** Its labels are inspected again after the pull, before `up`. A new hit is a
  Deny, with a reason that names the label key.
- **Persons.** Exposures from image labels found before `up` are added to that run's decided set, then audited and
  notified as `exposure_change`. The re-check does not count them as a violation.

### Shared namespaces and protected resources (review 0006-C)

- **Related, as with `volumes_from`.** The resolved x of a `container:<x>` / `service:<x>` (for `network_mode`, `pid`,
  `ipc`) goes into `Related` / `RelatedResources`, as with `volumes_from: container:`, and `EnsureDecided` covers it.
- **Test:** `network_mode: container:whiskers` through a webhook is refused; for an interactive Admin it ends in
  Confirm.

### A run that cannot be checked (review 0006-D)

- **Class `public`.** A container that cannot be inspected counts as an undecided exposure of class `public`.
- **"Created or recreated by this run"** means container IDs that were not in the plan's `Impact` before `up`, or whose
  `Created` is later than the start of `up`.
- **Fallback.** If even that cannot be listed, every container of the project counts.

### Smaller points

- **Registering `container.disable_restart`.** It is registered in `CapabilityCatalog`, `VerificationContracts` and
  `MutationGatewayCoverageTests`. Its level is not higher than that of the operations that compose it.
- **Pull.** The `pull` step stays (redeploys keep updating images). The image inspection runs after the pull and before
  `up`.
- **`AgentRuleSet`.** It means: some enclosing scope was decided by `CreateDefault`.
- **Additional rows, class `host`:** `security_opt: label=disable` and `systempaths=unconfined`, and
  `device_cgroup_rules`.
- **Additional tests:**
  - a failed stop is retried, then reported as "container still running";
  - a run that cannot be checked;
  - git deploy inspects the images it built;
  - `pid`/`ipc` with `service:x`.

## Amendment 5 — review round 5 (6 November 2026)

This amendment takes precedence over Amendments 1–4. All five amendments are to be accepted together.

### Stopping containers this run created (review MAJOR-1)

- **The new IDs join the decided set.** Before stopping, the re-check adds the container IDs this run created or
  recreated to the decided set of the enclosing scope. They are identified by these labels on the plan's server:
  - `com.docker.compose.project` = the plan's real project;
  - `com.docker.compose.service` ∈ the plan's services.
- **Narrow API.** This goes through a narrow `MutationScope` API that authorises only `container.disable_restart` and
  `container.stop`. No other target binding changes.
- **Test:** the stop of a container created by this run passes the scope guard; one from another project does not.

### Images between planning and `up` (review MAJOR-2)

- **Inspected again.** After the pull or the `build`, every image of the plan is inspected again, comparing image ID and
  labels. A hit of a Confirm class that is not in the decided set is a Deny under the agent rule set, whether or not the
  image was present locally when the plan was made.
- **No pull inside `up`.** Every `up` that follows an inspection runs with `--pull never`, and with `--no-build` after an
  explicit build. That way `up` pulls nothing itself (`env_set` and `env_file_save` included).
- **Files already written.** A refusal after the files were written restores the previous `.env` and compose files.
- **Tests:**
  - an image present locally whose pull brings a Funnel label: Deny before `up`;
  - `env_set` with a missing image does not pull inside `up`.

### Label values (review MAJOR-4)

- **Key only.** `Detail` and the reason hold the label key only.
- **When the value is shown.** Only for a fixed allow-list of shapes that are not secret: `true`/`false`, a port
  number, a Traefik `rule`. Every other value is `Withheld`: compared and bound, never written.
- **Test:** a literal `caddy.tls.dns` token appears neither in the audit nor on the approval card.

### Smaller points

- **`container:<x>` on nested decisions.** x is added to the plan's decided identities and carried in the nested
  decision's `related` argument. It is `Withheld` when it comes from a variable, and `EnsureDecided` checks it.
- **Containers that cannot be listed.** `docker update --restart=no` for every container that can be listed, then
  `docker compose -p <project> stop`. Anything that still cannot be stopped is reported as `exposure_violation` "not
  stopped".
- **The `Created` time clause is dropped.** The ID rule alone decides.
- **Payload name.** The `SinkRedaction.PayloadNames` entry is written normalised: `exposurewithheld`.
- **Additional tests:**
  - a local image's label found while planning gives Confirm over MCP;
  - for persons, image labels do not count as a violation.

## Amendment 6 — review round 6 (6 November 2026)

This amendment takes precedence over Amendments 1–5. All amendments are to be accepted together.

### Build before inspecting (review MAJOR-A)

- **Order on every path that runs a `ComposePlan`:**
  1. write the files;
  2. `pull` (images missing locally, or all images on paths that pull);
  3. build every service with `build:` (`BuildCommand`);
  4. inspect every image of the plan;
  5. `up --pull never --no-build`.
- **The window is gone.** The window for `build:` services named in Amendment 2 is withdrawn.
- **Test:** `deploy_compose` with a `dockerfile_inline` that sets a Funnel `LABEL` ends in Deny under the agent rule set,
  before `up`.

### Values from variables (review MAJOR-B)

- **Only literal values.** The allow-list of shapes in Amendment 5 applies only to values written literally in the
  compose file. A value that comes wholly or partly from a variable is always `Withheld`, whatever its shape.
- **Traefik paths.** Literal Traefik `rule` values with a `Path`/`PathPrefix` matcher are also `Withheld`.

### Smaller points

- **Stop set.** The IDs created by this run go into their own set. Only `container.disable_restart` and
  `container.stop` read it; `Covers` / `EnsureDecided` never do.
- **`relatedWithheld`.** Withheld `container:<x>` entries travel in `relatedWithheld`, which joins `PayloadNames` and
  `ProtectedResourceMatcher.PayloadArguments` (normalised: `relatedwithheld`). `HostAsync` splits `related` /
  `relatedWithheld` as it does `mounts`.
- **Comparison key.** Comparison and binding use `(service, kind, key, value)`. A value may be withheld. Withheld values
  travel in `exposureWithheld` / `__resolvedExposureWithheld*`.
- **Fallback stop.** The fallback `docker compose -p <project> stop` runs as a raw host command under the enclosing
  compose capability, and only under the agent rule set. It has a test.
- **README.** It names the window in which a re-tag between the inspection and `up --pull never` is caught only by the
  re-check after `up`.
