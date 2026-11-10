# Contract 0013 — Exposure changes, complete (INV-62)

Status: **Accepted** · 8 November 2026 by the maintainer, with D1–D18 as written and the open questions Q1–Q7 answered
as recommended (see "Decisions on the open questions" at the end). Extends [contract 0006](0006-exposure-changes.md)
(with all its amendments), which stays binding wherever this contract does not change it.

**Amendment 1** (10 November 2026, decided by the user on 2026-11-10): D10 pins every image by its **image id**
(`sha256:…`), never by a repo digest; U2, D9, D11, D12, Q6 and the tests of § 4 follow (see "Amendment 1" at the end).
**Amendment 2** (10 November 2026, accepted by the user on 2026-11-10): the host Traefik of Q1 is recognised by image name only.

## Purpose

INV-62 is ⚠️ for three reasons (`SECURITY_INVARIANTS.md`, `RELEASE_GATE.md`, README "remaining risks"):

1. **Two deployment paths bypass the compose plan.** The Deploy page (form tab and compose tab) and MCP `deploy_app`
   create plain containers through `IDockerService.CreateContainerAsync`. Nothing on them is decided as an exposure.
2. **The re-tag window.** A tag moved between the image inspection (step 4) and `up --pull never` is caught only by the
   re-check after `up`.
3. **Third-party routes.** Traefik `exposedByDefault`, cloudflared tunnels and Tailscale `serve`/Funnel in a sidecar
   publish services without a label on them; they are caught only by the re-check or not at all.

This contract closes all three, so that INV-62 can become ✅.

## Maintainer decisions (8 November 2026, fixed)

- **U1 — One way.** The Deploy page's compose tab **and** `deploy_app` (and with it the Deploy page's form tab, which
  builds the same `DeploymentRequest`) go through the same `ComposePlan` / `ComposeRunner` as every other deployment:
  they write a real compose project; plan, exposure decision, confirmation and re-check apply. Real compose projects
  replace single containers.
- **U2 — Digest pinning.** After the inspection, Whiskers pins every image `up` starts to ~~`image@sha256:<digest>`~~
  its image id `sha256:<id>` (Amendment 1) through an override file, so exactly the inspected image starts.
- **U3 — Third-party exposure.** The plan detects Traefik with `exposedByDefault`, cloudflared, and Tailscale
  `serve`/Funnel. They count as class `public` and need confirmation; anything unreadable refuses the plan (fail closed).

## Current state (verified at `9a12f7a3`)

- `DeploymentService.DeployFromFormAsync` pulls and calls `IDockerService.CreateContainerAsync` (`[MutatingOperation
  ("container.create")]`, `ContainerLifecycleOperations.cs:30`): `HostConfig` with `PortBindings`, `Binds`,
  `RestartPolicy`, `NetworkMode = NetworkName ?? "bridge"`, no labels. `DeployFromComposeAsync` parses YAML with
  `ComposeFileParser` (refuses `build:`, keeps `image`, `ports`, `volumes`, `environment`, `restart`, `network_mode`,
  silently ignores everything else) and creates one container per service, named `container_name` or the service name.
- `Deploy.razor` runs the form under `app.deploy` and the compose tab under `compose.deploy`, deciding containers with
  colliding names (`ComposeModel.Impact` with an empty project) — no `ComposePlan`, no exposure decision.
- MCP `deploy_app` (`ContainerTools.cs:281-361`) builds a `DeploymentRequest` from comma lists, **silently drops**
  malformed port and env entries, pulls and calls `CreateContainerAsync` directly. `MutationTargets.HostAsync` plans only
  `compose.deploy` (`:257`), so `app.deploy` from MCP carries no exposures and `ExposureChangeRule` never fires.
- `CapabilityCatalog`: `app.deploy` (tool `deploy_app`, Admin, Root, Long), `compose.deploy` (Admin, Root, Long),
  `container.create` (UI only, used only by `DeploymentService`). `MutationCompositions`: `app.deploy` →
  `image.pull`, `container.create`.
- The App Store already deploys under `app.deploy` through `ComposeImpact.DeployAsync` (a real compose project).
- `ComposeRunner` inspects images with `docker image inspect --format '{{.Id}}…{{json .Config.Labels}}'`
  (`ComposeExposure.cs:538`); `up` uses the tags.
- Other code that creates containers: `ContainerLifecycleOperations.RecreateContainerAsync` (`container.update`),
  `RollbackContainerAsync` (C12 rollback), `DockerClientHostShell.CreateAsync` (R1-57 host-shell helper), and raw
  `docker run` / `compose up` strings in `TrivyScanner`, `VolumeBackupService`, `MtlsAllowListCommands`,
  `OnboardingService`, AutoPatch `img-update.sh`.
- AutoPatch skips containers whose image contains `@sha256:` (`AutoPatchPlanner.cs:121`).

## Boundaries (fixed)

- Everything in 0006 stays: PC-D1 (a person is not asked to confirm their own action; audit and notification instead),
  the plan as the single source, `EnsureDecided`, withheld `.env` values, the agent-only stop.
- Capability ids and levels stay: the form and `deploy_app` are `app.deploy`, the Deploy page's compose tab is
  `compose.deploy`, both Admin / Root / Long. MCP tool levels stay Admin.
- No new way to create a container outside `ComposeRunner` on a deployment path. The non-deployment creators listed
  above stay, each named in the architecture test (§ 4, `DeploymentPathArchitectureTests`).

## 1. Deployment paths (U1)

**D1 — The form tab and `deploy_app` produce a compose file.** A pure mapper `DeploymentCompose.ToCompose(
DeploymentRequest) → (project, yaml)` in `Services/Deployment/` is the only translation; the page, the tool and
`MutationTargets` (D5) all call it, so the decision and the run see the same file. Mapping:

| Input | Compose | Refused when |
|---|---|---|
| `ContainerName` / `appName` | project = service = `container_name` = the name | not `^[a-zA-Z0-9._-]+$`, or its normalised project name (`ComposeModel.NormalizeProjectName`) fails `IsSafeProjectName` |
| empty name (form only) | name derived from the image's last path segment without tag/digest | the derived name is not valid |
| `Image` | `image:` (literal) | empty, contains `$` or whitespace |
| port `host→container` | `ports: ["<host>:<container>/tcp"]`; with a bind IP `"<ip>:<host>:<container>/tcp"` (IPv6 bracketed) | a port that is not 1–65535, an IP that does not parse. `deploy_app` no longer drops a bad entry: it refuses the call |
| volume `src:dst[:mode]` | short syntax; a source without `/` is a named volume declared top-level with `name: <src>` (same volume as before, not project-scoped) | not 2 or 3 parts, a relative path (`./x`), a mode outside `ro`, `rw`, `z`, `Z`, a `$` |
| env `KEY=VALUE` | `environment:` map, value YAML-quoted, `$` written as `$$` (no interpolation) | key not `^[A-Za-z_][A-Za-z0-9_]*$`, a duplicate key |
| restart policy | `unless-stopped`, `always`, `no`, `on-failure` → `on-failure:5` (as before) | any other value |
| network | empty or `bridge` → `network_mode: bridge` (as before, Q3); `host` / `none` → `network_mode`; any other name → `networks: { <n>: { external: true } }` | a name with `$` |

The generated file starts with the marker line `# whiskers: app.deploy` (ownership, D3). The `.env` is not written
(`EnvFileContent = null`).

**D2 — The compose tab is the compose editor's path.** It gets a required project name field (pre-filled from a
top-level `name:` or the first service) and runs `ComposeImpact.DeployAsync(…, "compose.deploy", …,
ComposeDeployment.FromContent(serverId, DeploymentDirectory(project), yaml))` exactly as `ComposeEditor.razor` does.
`build:`, `labels`, `cap_add` and everything else compose knows now work (and are decided) instead of being refused or
silently ignored. "Validate" becomes "Check plan": `IComposePlanner.PlanAsync` (read only) shows project, services,
existing containers touched, and every exposure with its class.

**D3 — Location and ownership.** Both new paths write to `/opt/deployments/<project>/docker-compose.yml`, like the App
Store, the editor and `deploy_compose`. The plan is refused (before anything is written) when:

- a compose project of that name exists with a different `com.docker.compose.project.working_dir`;
- (form and `deploy_app` only) the directory holds a `docker-compose.yml` without the `# whiskers: app.deploy` marker —
  another path owns it; the message points to the compose editor;
- a **legacy container** exists (D15).

A redeploy with the same name is a normal compose redeploy (`Recreate` impact), which the old path could not do
("name already in use").

**D4 — UI runs.** The form runs `ComposeImpact.DeployAsync(…, "app.deploy", new { serverId, appName, image, volumes },
…)` like the App Store; the compose tab as in D2. Progress shows the runner's steps ①–⑤ (`ComposeRunOptions.Progress`).
Success shows the project and its container names; a refusal (`ComposeRunResult.Refusal`) is a warning, violations an
error, both naming service, kind and key only.

**D5 — `deploy_app` through the gateway's plan.** `MutationTargets.HostAsync` plans `app.deploy` from the tool's
arguments (through D1's mapper) the same way it plans `compose.deploy` with `composeContent`: `WithPlanAsync` fills
`Related`, `Exposures` and `Withheld`, so `ExposureChangeRule` decides an agent's or API key's `deploy_app` (Confirm for
`ExposureConfirmClasses`). `RefusedFromCallers` applies to `app.deploy` on every surface but Ui and Automation. The tool
plans again, calls `EnsureDecided(MutationScope.Current)`, `ComposeImpact.ReportProject(plan)`, and runs
`IComposeRunner.RunAsync(plan, executor.ExecuteMutationAsync, new ComposeRunOptions(Pull: true, 5 min))`. A Kubernetes
server is refused ("deploy_app needs a Docker host").

**D6 — Removed code.** `IDeploymentService.DeployFromFormAsync` / `DeployFromComposeAsync`, `DeploymentService`'s create
logic, `ComposeFileParser` (and `ComposeFileParserPortTests`), `IDockerService.CreateContainerAsync`,
`ContainerLifecycleOperations.CreateContainerAsync`, the capability `container.create` (catalog, `VerificationContracts`,
`ContainerCreateVerifier` registration, `MutationGatewayCoverageTests`, `RootEquivalentAdminTests`). `IDeploymentService`
and `NoopDeploymentService` go; the Deployment module keeps templates and image search and still gates the page.

**D7 — Capabilities, compositions, verification.**

- Ids and levels unchanged (Boundaries).
- `MutationCompositions`: `app.deploy` → `container.disable_restart`, `container.stop` (replaces `image.pull`,
  `container.create`). 0006 Amendment 3 dropped `app.deploy` from the stop compositions because it was UI-only;
  `deploy_app` makes it agent-reachable again. `app.deploy` stays in `RawHostCommands`.
- `VerificationContracts["app.deploy"]` = "every container of the compose project runs", with the `compose.deploy`
  verifier (project from `ReportProject`).

**D8 — Audit (INV-45).** Each attempt is one gateway event, as today: the MCP call is one decision (its plan is in the
resolved target, no nested decisions); a UI run has its top-level event plus the nested mounts / exposure /
per-container decisions of 0006, each one event. `deploy_app` writes no row of its own; what it reports enriches the
gateway's event through `IToolAuditLog`, once per attempt, also for a refusal before planning (invalid input →
`MutationScope.Fail`). Env values never enter an event: `envVars` is already a payload (`SinkRedaction`), the generated
YAML is passed as `yaml` (payload) where an argument carries it.

**D9 — What MCP returns.** `deploy_app` returns: `App '<name>' deployed as compose project '<project>' on <server>`,
the containers (name and short id), the image and the image id it was pinned to (D10, Amendment 1), the published ports as decided.
A refusal returns `Deployment refused: <reason>`, violations `Deployment failed: <violations>` (as `deploy_compose`).
The old `Container ID:` line goes; the CHANGELOG names the output change.

## 2. Digest pinning (U2)

**D10 — The pin is the image id** (Amendment 1, 10 November 2026; replaces the repo-digest rule). Step 4's
`InspectImagesCommand` prints `.Id` and the labels (read from the whole `Config`) of each image, local only. For each
active service the pin is the inspected image id itself, `sha256:<id>` — for a pulled, a built, a `docker load`ed and a
locally tagged image alike. Repo digests are not read.

Consequence: a pinned container's image reference (`Config.Image`, the Image column of the container list) is the id
`sha256:…`; the name the project uses is kept in the label `whiskers.image.ref` (D11).

**Multi-arch.** No special case any more: the id is the image the store holds locally for the platform it pulled, and
D11 verifies it.

**D11 — Verify, write, use.** Between step 4 and step 5:

- `docker image inspect --format '{{.Id}}' <pin>` for every pin must return the inspected id;
- the pins are written to `<ProjectDirectory>/.whiskers/pin.override.yml` (services → `image: <pin>`, plus a label
  `whiskers.image.ref=<original reference>`), with the same backup/restore as the other written files;
- `UpCommand` (and the fallback `StopCommand`) add `-f` of that file after the planned files. `config`, `pull` and
  `build` never use it.

**A failed pin refuses** (before `up`, files and tags put back as for any step-4 refusal): an inspect that fails, an id
that differs, an id that is not `sha256:` + 64 hex, a pin file that cannot be written.

**D12 — The pin file is Whiskers' own.** It stays after the run (compose records it in
`com.docker.compose.project.config_files`) and is rewritten by every run. The planner drops a file of that name from
every file list it finds (directory or labels), so a redeploy plans and pulls the tags, never yesterday's pins. A
Git deploy checkout gets `.whiskers/` in `.git/info/exclude`.

**D13 — Re-check of the image.** The re-check after `up` compares each created container's `Image` (id) with the id
inspected for its service. A difference is an undecided exposure of class `public` (under the agent rule set: stopped,
as in 0006). The README's "re-tag window" risk is removed.

**D14 — Pinned containers and updates.** `container.update` and `autopatch.run` refuse a container carrying
`whiskers.image.ref` with "pinned by a Whiskers deployment — redeploy the project to update"; the update check compares
the remote digest of `whiskers.image.ref` with the running image (Q4). Side effect: a Whiskers-deployed container can no
longer be recreated from a new image outside the runner, which also closes 0006's "container.update and autopatch.run"
risk for such containers.

## 3. Third-party exposure (U3)

New `ExposureKind.ExternalRoute`; key = signal, value = `true` (host Traefik: its container name).
Detection runs at planning (from `docker compose config --format json` and the local images' `Config.Entrypoint`,
`Cmd`, `Env`, `Labels`), again at step 4 (images after pull/build, config files re-read and compared by hash; a changed
file refuses), and in the re-check (from container inspect: `Config.Entrypoint`, `Cmd`, `Env` keys, `Mounts`).

**Recognising a tool.** A service *is* tool T when its image repository's last path segment is T's name, or the
effective argv (compose `entrypoint` ?? image `Entrypoint`, then `command` ?? image `Cmd`) has T's binary as `argv[0]`
basename, or a `sh -c` / `bash -c` string contains T's binary as a word:

| Tool | Names / binaries | Extra signal |
|---|---|---|
| Traefik | `traefik` | — |
| cloudflared | `cloudflared` | env key `TUNNEL_TOKEN` or `TUNNEL_TOKEN_FILE` |
| Tailscale | `tailscale`; `containerboot`, `tailscaled`, `tailscale` | env key `TS_AUTHKEY`, `TS_AUTH_KEY`, `TS_SERVE_CONFIG` |

**Signals and classes:**

| Key | When | Class |
|---|---|---|
| `traefik.exposedbydefault` | a Traefik service of the plan with the `docker` or `swarm` provider enabled and `exposedByDefault` not `false` — read from **every** source (CLI `--providers.docker…`, env `TRAEFIK_PROVIDERS_DOCKER…`, the static file); any source that enables it counts (Traefik's default is `true`) | public |
| `traefik.exposedbydefault@host` | a running Traefik container on the server outside the plan's project, same rule on its inspect (Q1): every planned service without a literal `traefik.enable=false` label | public |
| `cloudflared.tunnel` | any cloudflared service (its ingress lives in Cloudflare or in its config; either way public) | public |
| `tailscale.funnel` | a serve config (`TS_SERVE_CONFIG`) with any `AllowFunnel` entry `true`, or `tailscale funnel` in a command | public |
| `tailscale.serve` | a serve config with `TCP`, `Web` or `Services` entries and no Funnel, or `tailscale serve` in a command | published |
| `tailscale.state` | a Tailscale service with persistent state (`TS_STATE_DIR` or a mount at `/var/lib/tailscale`) and no `TS_SERVE_CONFIG`: its serve state lives in tailscaled and cannot be read (Q7) | public |

The exposure is attached to the tool's service; the reason names the services sharing its network namespace
(`network_mode: service:<tool>`). `ExposureChangeRule` gets reasons such as "web becomes public through a Cloudflare
tunnel (cloudflared)" and "traefik routes every container on the server (exposedByDefault)". No value (token, key, URL)
is ever shown: the detail is the key only.

**D16 — Config files.** Traefik's static file is `--configFile`/`--configfile`, else the default paths
(`/etc/traefik/traefik.{yml,yaml,toml}`, `$XDG_CONFIG_HOME/traefik.*`, `$HOME/.config/traefik.*`, `./traefik.*`);
Tailscale's is `TS_SERVE_CONFIG`. A path is mapped through the service's mounts to a host path and read with
`IHostCommandExecutor.ExecuteAsync` (at most 1 MiB). YAML is parsed with YamlDotNet, JSON with `System.Text.Json`, TOML
only for `[providers.docker]` / `[providers.swarm]` tables and dotted `providers.docker.exposedByDefault` keys.

**Unreadable → the plan is refused**, for every principal: a value of these fields from an unresolved variable; a `sh -c`
string with a variable that may name a tool; a config file on a named volume, tmpfs, or inside a non-official image (the
official `traefik` image ships none); a host path that cannot be read, is larger than 1 MiB, or does not parse; a TOML
construct outside the subset; an image whose `Entrypoint`/`Cmd`/`Env` cannot be inspected. **Not** unreadable: a fact
that cannot be known by design (`cloudflared.tunnel`, `tailscale.state`) — it counts as `public` and is decided.

**D17 — False positives.** Never a silent pass. An agent or API key gets the confirmation; a person gets audit and
`exposure_change` (PC-D1). The way out is to make the configuration explicit: `--providers.docker.exposedbydefault=false`,
`traefik.enable=false` on a service, a `TS_SERVE_CONFIG` without Funnel. There is no per-project allow-list;
`ExposureConfirmClasses` stays the operator's only switch.

**D18 — Traefik labels.** `ExposureLabelPrefixes` default replaces `traefik.http.routers.` and `traefik.tcp.routers.`
by `traefik.` (Q5): `traefik.enable=true` alone gets a default router. A literal `traefik.enable=false` is no exposure.
The changed default changes every policy's `VersionHash` (open approvals expire at the upgrade; CHANGELOG).

## 4. Tests (acceptance)

- `DeploymentComposeTests` (mapper): every row of D1's table, the refusals (a bad port, an env key, a relative volume,
  `$` in image/volume, an unknown restart policy), `$` → `$$`, named volume keeps its name, the marker line,
  `bridge` default; the same request gives the same YAML on page, tool and target.
- `DeployAppComposeTests`: `deploy_app` with a Funnel label in the image or `TUNNEL_TOKEN` → Confirm with
  `MaxAutonomousLevel = "admin"`; an API-key run → refused; injected `exposure`/`mounts` arguments refused; a malformed
  port refused (no silent drop); a redeploy recreates; one gateway event per attempt; the output of D9.
- `DeployPageComposeTests` (bUnit): form and compose tab call `ComposeImpact.DeployAsync` with `app.deploy` /
  `compose.deploy`; ownership refusals of D3; legacy refusal of D15; "Check plan" lists exposures.
- `ComposePinTests`: the id pin for a pulled, a built and a loaded image (Amendment 1: never a repo digest), every refusal
  of D11, the pin file only in `up`/`stop`, the planner drops it, the
  re-check flags a different image id (D13), stop under the agent rule set.
- `ComposePinIntegrationTests` (Docker suite, `docker.txt`): on a real daemon with the minimum Compose (v2.15), a project
  with a pulled and a built service starts exactly the inspected ids under `up --pull never --no-build` with the pin
  file; a re-tag between step 4 and `up` changes nothing — both services still start the inspected images, also on
  Docker 29's containerd image store (Amendment 1).
- `ThirdPartyExposureTests`: each tool by image, by argv, by `sh -c`, by env key; every signal row; every source of the
  Traefik rule (CLI, env, YAML, TOML) incl. the default `true`; host Traefik; every "unreadable" case refuses; the
  by-design cases count `public`; `traefik.enable=false` exempts; no value in reason, audit or notification; step 4
  refuses a changed config file; re-check finds a signal from the container.
- `ExposureChangeRuleTests`, `ComposeExposureTests`: the new kind and reasons; the `traefik.` default; JSON round-trip of
  the policy.
- `UpdatePinnedContainerTests`: `container.update` / AutoPatch refuse a pinned container; the update check reads
  `whiskers.image.ref`.
- `DeploymentPathArchitectureTests` (mechanical, IL scan with `MutationCallScanner`'s reader):
  1. calls to `Docker.DotNet.IContainerOperations.CreateContainerAsync` exist only in `RecreateContainerAsync`
     (`container.update`), `RollbackContainerAsync` (C12) and `DockerClientHostShell.CreateAsync` (R1-57) — a new caller
     fails with its name;
  2. `IDockerService` has no method that creates a container;
  3. string literals (`ldstr`) containing `docker run` or `compose … up` occur only in `ComposePlan` and the listed
     non-deployment types (`TrivyScanner`, `VolumeBackupService`, `MtlsAllowListCommands`, `OnboardingService`,
     `LogHygieneAdvice`), and the embedded `img-update.sh`;
  4. every MCP tool mapped to `app.deploy`, `compose.deploy`, `gitdeploy.deploy`, `container.env_set` references
     `IComposeRunner.RunAsync`.

## 5. Migration of existing containers

**D15 — Legacy containers are left alone and block a clash.** Containers the old paths created have no compose labels.
Nothing converts or removes them. A plan of the two new paths is refused before anything is written when an unlabelled
container has the name a new container would get, or — for the compose tab — the name the old path would have used
(`container_name` or the service name), since the new names (`<project>-<service>-1`) would otherwise run side by side
with it. The message names the container and the ways out: remove it on the container page, or choose another name
(Q2). The CHANGELOG and the README's upgrade note describe this.

## 6. Gate and documentation

- `SECURITY_INVARIANTS.md` INV-62: statement extended by "or routes through a recognised third-party proxy or tunnel
  (Traefik `exposedByDefault`, cloudflared, Tailscale serve/Funnel)" and "no deployment path creates a container outside
  the compose runner"; status ✅; evidence rewritten; tests column adds `DeploymentComposeTests`, `DeployAppComposeTests`,
  `ComposePinTests`, `ComposePinIntegrationTests`, `ThirdPartyExposureTests`, `DeploymentPathArchitectureTests`.
- `RELEASE_GATE.md`: the INV-62 row leaves *Open invariants*; §11 counts in `SECURITY_INVARIANTS.md` follow.
- `.github/required-tests/in-process.txt` and `docker.txt`: the generated block regenerated (every test of a ✅
  invariant, method by method).
- `PATH_CAPABILITIES.md` rows U-10/U-11 and the MCP row of `deploy_app`; `CAPABILITY_INVENTORY.md` regenerated
  (`container.create` gone).
- README "Compose exposure checks: remaining risks": "Out of scope", "the re-tag window", "Traefik exposedByDefault",
  "Tailscale sidecar / cloudflared" go; new lines: images that run a proxy or tunnel behind an unrecognisable
  entrypoint; Tailscale serve state is read from configuration, never from tailscaled at run time. Upgrade note: real
  compose projects, legacy clash, pinned images and updates, `traefik.` default, policy hash.
- `docs/features.md`, `Services/Deployment/README.md`, `Modules/Deployment/README.md`, `docs/modules/deployment.md`,
  `Services/Mutations/README.md`, `Services/Guardrails/README.md`, CHANGELOG (behaviour changes: real projects,
  `deploy_app` output and refusals, `bridge`, pinning, AutoPatch/update on pinned containers).

## 7. Work split (one PR each, docs in the same PR)

1. **Pinning** (D10–D14): `ComposeRunner`, `ComposeExposures`, planner, update/AutoPatch handling,
   `ComposePinTests`, `ComposePinIntegrationTests`, `UpdatePinnedContainerTests`; README re-tag risk removed.
2. **Third-party detection** (D16–D18): `ExternalRoute`, recognisers, config readers, host Traefik, rule reasons,
   re-check rows, `traefik.` default, `ThirdPartyExposureTests`.
3. **One way** (D1–D9, D15): mapper, `MutationTargets` for `app.deploy`, `deploy_app`, `Deploy.razor`, removals,
   compositions, verifier, architecture test, the gate flip of § 6 (the last PR, since a listed ✅ would fail as stale).

## Plan triage

- [Claude-prüfbar] D1–D18, every test in § 4 (the Docker suite runs in CI), gate and docs.
- [braucht Nutzer/Hardware] (1) On a real host: a Traefik with `exposedByDefault`, a cloudflared tunnel and a Tailscale
  Funnel sidecar each end in Confirm over MCP and in an audited `exposure_change` from the UI. (2) An existing legacy
  container from the old Deploy page meets the refusal of D15. (3) A multi-arch image on an arm64 host pins and starts.

## Decisions on the open questions (maintainer, 8 November 2026)

- **Q1 — decided: detect a Traefik already running on the host.** Row `traefik.exposedbydefault@host` applies. An
  unreadable configuration of that host Traefik counts as `exposedByDefault` and is confirmed (class `public`), it does
  **not** refuse the plan — the host Traefik is not part of the deployment. This is the one exception to U3's "anything
  unreadable refuses"; every unreadable source inside the plan's own project still refuses (D16).
- **Q2 — decided: legacy containers with a colliding name are refused** with a clear message naming the container and
  the ways out (D15). No "replace" option.
- **Q3 — decided: the form and `deploy_app` keep `network_mode: bridge`** (D1).
- **Q4 — decided: `container.update` and AutoPatch refuse pinned containers** with a clear message; a pinned container
  is updated by redeploying its project (D14). **Known follow-up:** a contract after 1.0 lets `container.update` and
  AutoPatch run through `ComposeRunner`, so pinned containers can be updated in place again.
- **Q5 — decided: Traefik label prefix `traefik.`**, except a literal `traefik.enable=false` (D18).
- **Q6 — decided: a build-only image pins to its image id** (`sha256:<id>`); since Amendment 1 every image does. The Whiskers tag
  `whiskers-pin/<project>-<service>:<id12>` is used only if Compose rejects the id, and is still verified by id.
- **Q7 — decided: a Tailscale sidecar without `TS_SERVE_CONFIG` counts as `public` and is confirmed**
  (`tailscale.state`).

## Amendment 1 — pin by image id (10 November 2026, decided by the user on 2026-11-10)

**Why.** Under Docker 29 with the containerd image store (the default of a fresh install) a reference
`<repository>@sha256:<digest>` resolves only through an image record of that repository that still points to the
digest. A tag moved after step 4 takes that record along: `up --pull never` then fails with "No such image" instead of
starting the inspected image. The image id resolves as long as the image exists, whatever its tags.

**Changed.** D10: the pin is always the inspected image id; repo digests are no longer read (`InspectImagesCommand`
prints the id and the `Config` only). U2, D9 (the MCP output names the image id), D11 (refusal for an id that is not
`sha256:` + 64 hex), D12 (wording), Q6 and the § 4 tests follow: `ComposePinTests` loses the repo-digest, normalisation
and multi-arch cases; `ComposePinIntegrationTests` proves that with both tags moved after the inspection both services
start the inspected images, on the classic and on the containerd store. The Image column of a pinned container shows
the id; the project's image name stays in `whiskers.image.ref`, which the update check (D14) reads. Q6's fallback tag
stays a fallback, used only if Compose rejects an id.

## Amendment 2 — host Traefik by image name (10 November 2026, accepted 2026-11-10)

**Why.** Recognising a Traefik on the host by argv (`sh -c`, `argv[0]`) would mean a `docker inspect` of every running
container outside the project on every plan; a container that cannot be inspected would then count as a routing
Traefik (Q1) and turn every agent deployment on that server into a confirmation.

**Changed.** Row `traefik.exposedbydefault@host` (and Q1): a running container outside the plan's project is a host
Traefik when its image name — the listed image, or `whiskers.image.ref` of a pinned container — is `traefik` (last path
segment, without tag or digest). Only those are inspected; the rule of row `traefik.exposedbydefault` then applies to
their inspect, and an unreadable one counts as routing (Q1 unchanged). A Traefik on the host behind an image of another
name is not recognised; the README names this as a remaining risk. Inside the plan's own project every recognition rule
of § 3 stays (image, argv, `sh -c`, environment key).

**Interpretation, no change.** "A value of these fields from an unresolved variable" (D16) covers a service's `image`,
`entrypoint`, `command`, `working_dir` and the values of the environment keys `TS_*`, `TUNNEL_*`, `TRAEFIK_*`, `HOME` and
`XDG_CONFIG_HOME`, as the plan model reads them before compose runs.

## Open questions (as drafted; answered above)

- **Q1 — Traefik already running on the host** (outside the project). Recommended: detect it (row
  `traefik.exposedbydefault@host`); its configuration being unreadable counts as `exposedByDefault` (confirmation), not
  as a refusal, because it is not part of the deployment. Alternative: only Traefik inside the project (the main risk
  the README names stays).
- **Q2 — Legacy containers.** Recommended: refuse with a clear message (D15). Alternative: an explicit "replace"
  option that renames and stops the old container before `up` and restarts it if `up` fails.
- **Q3 — Network of the form and `deploy_app`.** Recommended: `network_mode: bridge` as before. Alternative: compose's
  project network (DNS between services; behaviour change for apps that rely on the bridge).
- **Q4 — Updates of pinned containers.** Recommended: refuse `container.update` / AutoPatch with "redeploy the project"
  (D14), and a follow-up contract that routes both through `ComposeRunner`. Alternative: the follow-up now, in this
  package.
- **Q5 — Traefik label default.** Recommended: `traefik.` with the `traefik.enable=false` exemption (D18). Alternative:
  keep the router prefixes and add only `traefik.enable`.
- **Q6 — Pin of a build-only image.** Recommended: the image id (`sha256:<id>`), proven by the integration test.
  Fallback only if Compose rejects it: a Whiskers tag `whiskers-pin/<project>-<service>:<id12>`, still verified by id.
- **Q7 — Tailscale sidecar without `TS_SERVE_CONFIG`.** Recommended: `public` and decided (`tailscale.state`).
  Alternative: refuse as unreadable, which refuses every redeploy of such a sidecar.
