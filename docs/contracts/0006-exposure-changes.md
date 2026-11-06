# Contract 0006 — Exposure changes in compose deployments

Status: **Accepted** · 6 November 2026 by the maintainer, with the decisions D1–D6 at the end as recommended.

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
