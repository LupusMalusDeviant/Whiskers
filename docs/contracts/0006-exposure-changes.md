# Contract 0006 — Exposure changes in compose deployments

Status: **Accepted** · 6 November 2026 by the maintainer, with the decisions D1–D6 at the end as recommended.
**Amendments 1 and 2** (review rounds 1 and 2, at the end) are pending acceptance.

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

## Amendment 1 — review round 1 (6 November 2026, pending acceptance)

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

## Amendment 2 — review round 2 (6 November 2026, pending acceptance together with Amendment 1)

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
