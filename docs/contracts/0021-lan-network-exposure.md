# Contract 0021 — LAN networks as an exposure (macvlan / ipvlan)

Status: **Accepted by the maintainer on 16 November 2026; all decisions as recommended.**

The direction was decided by the maintainer on 16 November 2026: a new exposure kind **`LanNetwork`** in the detection
table of [contract 0006](0006-exposure-changes.md). This contract fixes how it is built. It **amends contract 0006**
(with contracts 0013 and 0017, which stay binding wherever this contract does not change them) and carries out the
item that contract 0017 deferred as A3 ("`macvlan` / `ipvlan` networks … a decision of its own (1.1.0)").
Contract 0006 itself is not edited by this draft; the text that is appended to it when this contract is built is
given in § 10.

Scope:
- **Protected areas, unchanged:** the auth middleware and its order, the OAuth whitelist, the mTLS/WireGuard layer
  and **the docker-socket-proxy verb whitelist**. `network create` and `network connect` are verbs the proxy already
  allows (Whiskers' own `create_network` / `connect_container_to_network` use them today); nothing here adds,
  removes or widens a verb. The only new read is `GET /networks` (network list/inspect), which the inventory already
  uses (`IDockerService.ListNetworksAsync`).
- **No DB change, no new configuration key.**

## Purpose

A `macvlan` or `ipvlan` network gives a container its own MAC/IP address on the physical LAN of its host. Traffic to
and from that address does not pass the host's iptables chains that Docker maintains for published ports, and no
`ports:` entry is needed: every port the container listens on is reachable from the LAN. For a host behind a firewall
that only forwards published ports, this is a bigger exposure than a published port.

Today none of the four paths that can bring a container onto such a network names that change:

1. a compose network with `driver: macvlan` or `driver: ipvlan`;
2. an `external: true` network that already exists on the host with one of these drivers;
3. MCP `create_network` / the Networks page creating such a network;
4. MCP `connect_container_to_network` attaching a container to one.

## Current state (verified at `ccd5137e`)

- **Detection table.** `ExposureKind` (`Services/Mutations/ComposeExposure.cs:7-29`) has `PublishedPort`, `HostNetwork`,
  `Privileged`, `AddedCapability`, `HostNamespace`, `RouteLabel`, `Device`, `SecurityOpt`, `DeviceCgroupRule`,
  `ExternalRoute`, `SocketMount`. None describes a LAN network.
- **Compose model.** `ComposeModel.ExposuresOf` (`ComposeModelExposures.cs:20-54`) reads top-level `networks:` only
  through `TopNetwork` (`:70-80`), which keeps `(Name, External)` and ignores `driver`. The only network rule is "an
  external network named `host`" (`:46-51`). A service without a `networks:` key (the implicit `default` network) is not
  looked at.
- **Compose config.** `ComposeExposures.FromConfig` (`ComposeExposure.cs:419-485`) gets the resolved top-level networks
  (`ComposeConfigEffects.cs:141`, `:150`) but only calls `IsExternalHostNetwork` (`:488-497`). The resolved `driver` is
  ignored.
- **External networks.** Nothing reads the live driver of an external network at planning time.
- **Re-check after `up`.** `ComposeExposures.FromInspect` (`ComposeExposure.cs:518-577`, called from
  `ComposeRunner.cs:448` and, for `container:x`, `ComposePlanner.cs:304`) reads `HostConfig.NetworkMode` only for
  `host` and `container:<id>`. `NetworkSettings.Networks` is not read.
- **`network.create`.** Catalog `CapabilityCatalog.cs:246` (Admin, Reversible, Short). MCP `create_network`
  (`Mcp/Tools/NetworkTools.cs:41-71`) accepts any `driver` string, described as "bridge, overlay, macvlan"
  (`:49`), and passes it unchecked. The Networks page offers `bridge`, `overlay`, `macvlan`
  (`Components/Pages/Networks.razor:106-110`) and runs `Mutations.RunAsync("network.create", …)` (`:172-189`).
  `NetworkOperations.CreateNetworkAsync` (`Services/Docker/Operations/NetworkOperations.cs:68-77`) sends only `Name` and
  `Driver` — no `Options`, no `ConfigFrom`, no `Internal`.
- **`network.connect`.** Catalog `CapabilityCatalog.cs:248` (Admin, Reversible, Short). MCP only
  (`NetworkTools.cs:100-115`); there is no UI path. The tool passes the caller's raw `networkId` to
  `IDockerService.ConnectContainerToNetworkAsync` (`[MutationTarget] networkId`, `IDockerService.cs:141-142`).
- **Target resolution.** `MutationTargetResolver.NetworkAsync` (`MutationTargets.cs:530-582`) passes `network.create`
  with the server only (`Creates`, `:146-149`) and resolves the network of `network.connect` from
  `ListNetworksAsync`, which already carries `Driver` and `Internal` (`Models/NetworkInfo.cs:3-15`). It fills no
  `Exposures`.
- **The rule.** `MutationPolicy.WithResolvedTarget` (`MutationPolicy.cs:127-155`) hands `Target.Exposures` of **any**
  capability to `ExposureChangeRule` as `__resolvedExposure*`. The rule (`Guardrails/ExposureChangeRule.cs:31-55`)
  confirms for the classes in `ExposureConfirmClasses`, default `public`, `host` (`Configuration/GuardrailPolicy.cs:93`).
  `PublishedPort` is class `published` (`ComposeExposure.cs:83-89`) and therefore **not** confirmed by default
  (contract 0006, D3).

## Goals and non-goals

**Goals**

- G1. `LanNetwork` is a row of the detection table, read by the plan model, by `docker compose config`, by a live read
  of the host's networks, and by the re-check after `up`.
- G2. `network.create` with a LAN driver and `network.connect` to a LAN network carry a `LanNetwork` exposure in the
  resolved target, so `ExposureChangeRule` decides them on the agent's path exactly as it decides a compose exposure.
- G3. Fail closed: a driver Whiskers does not know, a network it cannot read, an external network it cannot find.
- G4. Persons are not asked to confirm their own action (PC-D1); they get the audit entry and the `exposure_change`
  notification, as for every other `host`-class exposure.

**Non-goals**

- No change to how a LAN network is set up (parent interface, subnet, `driver_opts`). Whiskers' own create path keeps
  sending `Name` and `Driver` only.
- No new driver in the Networks page's list and no new MCP parameter.
- No change to `execute_command` (`host.exec`): a `docker network create -d macvlan` typed into a shell stays a Root
  host command, decided as such (INV-24/59), not as an exposure.
- No change to Kubernetes servers (the network tools are Docker only).

## 1. Interfaces

### 1.1 The kind

```csharp
public enum ExposureKind
{
    // … existing members unchanged …

    /// <summary>Contract 0021: the container gets its own address on the host's physical LAN (a macvlan / ipvlan
    /// network, or a driver Whiskers does not know). Key = the network's name as Docker knows it on the host, value = the
    /// driver. Class <c>host</c>.</summary>
    LanNetwork,
}
```

- `ExposureClasses.Of(LanNetwork, …)` = `host` (decision D1).
- `ExposureClasses.Noun(LanNetwork)` = `"LAN network"`.
- `ComposeExposures.Describe` = `"<service>: LAN network <key> (<value>)"`.
- `ExposureChangeRule.Reason`:
  `"<service> gets its own address on the LAN through network '<key>' (driver <value>), past the host's firewall."`;
  withheld: `"<service> gets a LAN network (withheld) of class 'host'; it needs a confirmation."`.
- `IsOwnOnly` is **not** extended: a service that shares the network namespace (`network_mode: service:x` /
  `container:x`) of a LAN-attached service inherits the exposure, because it is reachable at the same address.

### 1.2 Which drivers count

```csharp
/// <summary>Contract 0021: whether a network driver puts a container on the LAN.</summary>
public static class LanDrivers
{
    /// <summary>Drivers that never give a container a LAN address of its own.</summary>
    public static readonly IReadOnlySet<string> NotLan = new HashSet<string>(StringComparer.Ordinal)
        { "bridge", "overlay", "host", "none", "null" };

    /// <summary>True for macvlan, ipvlan and every driver not in <see cref="NotLan"/> (decision D2).</summary>
    public static bool IsLan(string driver);
}
```

- **Comparison is ordinal and exact** after trimming. Docker looks up built-in drivers by their exact lower-case name;
  any other spelling (`Macvlan`, `MACVLAN`) is not the built-in driver and is resolved by the daemon as a plugin name.
  Such a spelling is therefore "not in `NotLan`" and counts as LAN — the safe side whichever way the daemon resolves it.
- **Plugin-qualified names** (`vendor/driver`, `vendor/driver:latest`, `ghcr.io/x/y:tag`, a legacy plugin socket name)
  are not in `NotLan` and count as LAN (D2).
- **`host`** as a live driver is the one predefined host network; a service on it is already `HostNetwork` (contract
  0006, Amendment 1, external network named `host`). It is in `NotLan` so it is not counted twice.
- **`null`** is the driver Docker reports for a **config-only** network (`--config-only`). A container cannot attach to
  a config-only network, so it is in `NotLan`. A network created with `--config-from <cfg>` reports its own driver
  (`macvlan` / `ipvlan`) and is counted by that. Whiskers' create path cannot pass `ConfigFrom`
  (`NetworkOperations.cs:71-75`), and the compose specification has no `config_from`; such networks reach a plan only
  as external networks (§ 1.4), where the live driver decides.
- **Windows drivers** `transparent` and `l2bridge` also attach containers to the physical LAN; they are not in `NotLan`
  and so count. `nat`, `ics` and `l2tunnel` are not added to `NotLan` either (no Windows host is supported; D2 covers
  them).
- **`internal`.** A macvlan / ipvlan network created without a `parent` option, or with `internal: true`, is attached
  to a dummy link by Docker and does not reach the LAN. It still counts (D3), with `" internal"` appended to the shown
  detail only (not to the comparison value).

### 1.3 Plan model (`ComposeModel.ExposuresOf`)

- `TopNetwork` returns `(Name, External, Driver?)`. `Driver` is the interpolated `driver:` of the top-level network.
  A `driver:` that uses a variable the `.env` does not set refuses the plan, like every other network field.
- **Which networks a service joins.** The keys of its `networks:` (list or map form). A service with neither
  `networks:` nor `network_mode` joins `default`. A service with `network_mode: none|host|bridge|service:x|container:x`
  joins no project network.
- **Non-external network.** `LanNetwork` when its declared `Driver` is set and `LanDrivers.IsLan(Driver)`. The key is
  the network's real name (`name:` or `<project>_<key>`); the value is the driver. Withheld when the driver or the name
  came from a variable.
- **External network** (and a non-external one that already exists on the host, see § 1.4): decided by the live
  driver.
- `network_mode: <name>` with a value other than `none`, `host`, `bridge`, `default`, `service:x`, `container:x` is a
  network name; it is decided by the live driver like an external network.

### 1.4 Compose config and the live networks (`ComposeConfigEffects`, `ComposePlanner`)

- `FromConfig` adds `LanNetwork` for each network the service joins (`services.<s>.networks` keys, which compose fills
  with `default` itself) whose resolved top-level `driver` is LAN. As every entry from `config`, it is withheld unless
  the model read the same identity (contract 0006, Amendment 1).
- **Live read, once per plan.** `ComposePlanner.PlanAsync` calls `IDockerService.ListNetworksAsync(serverId, ct)` (the
  inventory read the resolver already uses; Docker API `GET /networks`, no shell) after `docker compose config`.
- **Which networks are looked up.** Every network a service joins, by the name compose resolved
  (`networks.<n>.name`): external networks always; non-external ones too, because compose reuses a network that
  already carries its name.
- **Result.** For each looked-up network:
  - found exactly once by name → `LanNetwork` when `LanDrivers.IsLan(live.Driver)`;
  - an external network not found → the plan is refused: "The external network '<name>' does not exist on <server>."
    (compose would refuse `up` the same way; a network created between planning and `up` is caught by step 4 and the
    re-check);
  - a non-external network not found → the declared driver decides (compose will create it);
  - found more than once (same name, different scope) → refused, "is not unique";
  - the list cannot be read (exception, cancellation, timeout) → refused: "The networks of <server> cannot be read, so
    a LAN network cannot be ruled out." **Fail closed.**
- **Union.** Declared and live LAN entries are united by identity, as model and config entries already are.
- **Shared namespaces.** `FromInspect` (used for `container:x`, `ComposePlanner.cs:304`) gains a `networks` parameter
  (`IReadOnlyList<NetworkInfo>`) and reads `NetworkSettings.Networks` of x: each attached network whose live driver is
  LAN (looked up by `NetworkID`) is a `LanNetwork` of x, inherited as every other row of x.

### 1.5 Runner (`ComposeRunner`)

- **Step 4 (inspect).** Next to the images, the runner reads the live networks again (`ListNetworksAsync`) and
  recomputes the `LanNetwork` entries of § 1.4 for the external and pre-existing networks. A new LAN entry of a confirm
  class that is not in the decided set is a **Deny** under the agent rule set, before `up` (files and tags restored as
  for an image-label hit, contract 0006 Amendments 5–10). For persons it is added to the decided set, audited and
  notified as `exposure_change`. Unreadable → Deny under the agent rule set, audited and notified for persons.
- **Re-check after `up`.** For each created container: `NetworkSettings.Networks` → for each entry, its `NetworkID`
  looked up in a fresh `ListNetworksAsync`; a LAN driver gives `LanNetwork` with key = the network's name, value = the
  driver. Compared like every other row (Runtime ⊆ decided, Amendment 2).
  - A network ID that is not in the list, or a list that cannot be read: the container counts as an undecided
    exposure of class `host` ("its networks could not be checked"). Under the agent rule set it is stopped,
    `exposure_violation` always (contract 0006, Amendments 2–5).
- **Comparison key row** (appended to the table of contract 0006, Amendment 7/8):

  | Kind | key | value |
  |---|---|---|
  | `LanNetwork` | the network's name on the host | the driver, exactly as Docker reports it |

### 1.6 `network.create` and `network.connect` (resolver, tools, page)

- **Resolver, `network.create`.** `NetworkAsync` still passes the target with the server only, and adds one exposure
  when `LanDrivers.IsLan(driver)`: identity `"network <name>: LanNetwork <name>=<driver>"` (service part = `network
  <name>`). `driver` is the operation's own argument (absent → `bridge`, the method's default). Nothing is withheld:
  name and driver are typed by the caller and already audited (`Details: Driver: …`).
- **Execution, `network.create`.** `DockerService.CreateNetworkAsync` calls a new scope check before the Docker call:

  ```csharp
  /// <summary>Contract 0021: the LAN exposure this call produces was decided in the open scope; refused otherwise.
  /// A call whose driver is not LAN passes.</summary>
  public static void EnsureLanDecided(string serverId, string networkName, string driver);
  ```

  A delegate that creates a different driver than the one decided (a value captured before the approval, a caller
  that changes it) is refused before Docker is called, like `EnsureDecided` for compose.
- **Resolver, `network.connect`.** The network is resolved as today; when `LanDrivers.IsLan(network.Driver)` the target
  gets `"<container name>: LanNetwork <network name>=<driver>"`.
- **Execution, `network.connect`.** `NetworkTools.ConnectContainerToNetwork` connects by the **resolved** network id
  (`MutationScope.Current` target id), not the caller's `networkId`. A network removed and re-created under the same
  name between decision and run has another id and fails. (Today the raw name passes `[MutationTarget]`, which accepts
  id, name or alias.) The resolved network id is part of the `ApprovalBinding`, so an approval names one network id,
  never a name. The driver of a network id cannot change, so no further check is needed.
- **MCP descriptions.** `create_network`'s `driver` description becomes: "Network driver: bridge (default), overlay,
  macvlan, ipvlan. macvlan, ipvlan and any other driver put containers on the host's LAN and need a confirmation for
  the agent." `connect_container_to_network`'s description adds: "Connecting to a macvlan/ipvlan network needs a
  confirmation for the agent."
- **MCP results.** Unchanged on success. A Confirm is answered as every other Confirm of the gateway (approval card,
  the reason of § 1.1).
- **UI flow (Networks page).**
  1. The person chooses `macvlan` in the driver list.
  2. A warning line appears under the list: "Containers on a macvlan network get their own address on the LAN; the
     host's firewall does not protect them." (new resx key `Networks_Hint_LanDriver`, EN and DE).
  3. "Create" runs `network.create` as today (no extra dialog, PC-D1).
  4. After success, one `exposure_change` notification (kind `LanNetwork`, the network name, the driver; no service).

### 1.7 Notification

`exposure_change` (suppressible) carries for a network operation: the server, the kind `LanNetwork`, the network name
and the driver. It is sent for persons only (the agent's path asked for confirmation instead), once per operation,
and never after a failed operation. No new event type.

## 2. Security model

| Who | Path | What decides | Result for a LAN network |
|---|---|---|---|
| Agent / API key | MCP `deploy_compose`, `deploy_app`, `set_container_env` | `CreateDefault()`, `ExposureChangeRule` on the resolved target | Confirm (class `host`); a key-owned unattended run is refused |
| Agent / API key | MCP `create_network`, `connect_container_to_network` | same | Confirm |
| Automation owned by a key | compose webhook, Git deploy | `CreateDefault()` | Confirm → Deny (no person) |
| Person (UI) | compose editor, App Store, Deploy page, `.env` save, Networks page | `CreateForHumans()` (PC-D1) | no confirmation; nested decision / gateway event audited; `exposure_change` |
| Automation owned by a person | compose webhook, Git deploy | `CreateForHumans()` | runs; audited; `exposure_change` |

- **Roles and levels unchanged:** `network.create`, `network.connect` stay Admin (`McpPermissionLevels.Admin`,
  catalog `A`); `compose.deploy` / `app.deploy` stay Admin / Root.
- **Gateway.** Every path already goes through the Mutation Gateway; this contract only adds entries to
  `MutationTarget.Exposures`, which are part of the `ApprovalBinding` (contract 0006, Amendment 1). For `network.connect`
  the `ApprovalBinding` also holds the **resolved network id** (`Target.Id`): an approval for network id X does not
  cover a network re-created under the same name (another id), which is decided again.
- **Under the standard policy** (`MaxAutonomousLevel = "read"`) all these capabilities are Confirm already. The new
  entries matter under an Admin-autonomous policy, where they still confirm, and they put the LAN reason on the card.
- **Operator switch.** `ExposureConfirmClasses` stays the only switch. An operator who removed `host` from it has
  turned off confirmation for every host-level exposure, `LanNetwork` included.
- **Audit.** No new event. The compose paths audit `LanNetwork` in the nested `exposure` / `exposureShown` arguments
  (withheld entries only as their length, `SinkRedaction`). `network.create` / `network.connect` audit through their
  existing gateway event (arguments `name`, `driver`, `networkId`, `containerId`).
- **Secrets.** None are touched. A network name or driver from an `.env` variable is withheld exactly as other
  exposures are (contract 0006, Amendments 3–6): compared and bound, never written.
- **Fail closed.** Unknown driver → LAN (D2). Unreadable network list → plan refused / Deny at step 4 / undecided
  `host` at the re-check. External network missing or ambiguous → plan refused. An exposure entry the rule cannot parse
  → Confirm (existing behaviour).

## 3. Invariant and § 7 impact

### Invariants

**No new row.** `INV-62` ("an exposure change is decided as such") is extended; LAN reachability is an exposure change
of the same kind as a published port or the host network, and the network operations belong to it. Contract 0020's
new row INV-66 is not touched.

**Edited when built (release-gate rule R5).** INV-62 is ✅, so its row is edited only in the implementing PR, together
with tests that exist and pass in that PR. No planned test may stand on a ✅ row. Until then the row stays exactly as
it is; the text below is what the implementing PR writes.

INV-62 statement, after "takes the host's network,": insert **"or an address of its own on the host's LAN (a
`macvlan`/`ipvlan` network or a driver Whiskers does not know, declared, external or attached later),"** and append
to the end of the statement: **"Creating such a network or connecting a container to one is decided as the same
exposure."**

INV-62 evidence, appended: "Since contract 0021: `LanNetwork` (class `host`, key the network name, value the driver)
is read from the model, `docker compose config` (with the implicit `default` network) and the host's live networks (an
external network missing, ambiguous or unreadable refuses the plan), again at step 4 and in the re-check
(`NetworkSettings.Networks` by network id; unreadable → undecided `host`); `network.create` with a LAN driver and
`network.connect` to a LAN network carry it in the resolved target; any driver outside `bridge`, `overlay`, `host`,
`none`, `null` counts."

INV-62 tests, appended in the implementing PR: the tests of § 4 as they exist there (never before).

**Status.** INV-62 stays ✅: its current statement does not claim LAN networks, and the gap is named in the README's
remaining risks until this contract is built (D6). The row is edited when built, not at acceptance.

### § 7 path matrix

No new path: every path exists. **Total unchanged** — 116 after contracts 0018 and 0019.

| Row | Change |
|---|---|
| M-19 `create_network` | invariant column `—` → `62 (a LAN driver is a LanNetwork exposure; Confirm for the agent, contract 0021)` |
| M-21 `connect_container_to_network` | `—` → `62 (connecting to a LAN network is a LanNetwork exposure; connects by the resolved network id, contract 0021)` |
| U-13 Networks create/remove | `—` → `62 (a LAN driver is audited and notified as exposure_change, PC-D1, contract 0021)` |
| M-05, M-06, M-07, S-04 and the compose UI rows | text unchanged; INV-62 already listed and now covers `LanNetwork` |

## 4. Test plan

**Unit (`ComposeExposureTests`)**

- `A_macvlan_network_in_the_file_is_a_lan_exposure_of_every_service_on_it`
- `An_ipvlan_default_network_reaches_services_without_a_networks_key`
- `A_driver_from_an_unset_variable_refuses_the_plan`
- `A_driver_from_a_variable_is_withheld`
- `A_bridge_or_overlay_network_is_no_exposure`
- `An_unknown_or_plugin_qualified_driver_counts_as_lan` (`vendor/net:latest`, `Macvlan`, `weave`)
- `A_config_only_network_driver_null_is_no_exposure`
- `Compose_config_driver_is_read_and_withheld_unless_the_model_read_it`
- `Network_mode_naming_a_network_is_decided_by_its_live_driver`
- `A_service_sharing_the_network_namespace_of_a_lan_service_inherits_it`
- `A_container_x_attached_to_a_lan_network_passes_it_on`
- `LanDrivers_is_ordinal_and_exact`

**Unit (`ExposureChangeRuleTests`)**

- `A_lan_network_needs_a_confirmation_and_names_network_and_driver`
- `A_withheld_lan_network_names_kind_and_class_only`
- `Without_host_in_the_confirm_classes_a_lan_network_is_allowed`
- `Not_part_of_CreateForHumans` (existing test extended by the kind)

**Planner / resolver (`ComposePlannerTests`, `MutationTargetResolverTests`)**

- `An_external_macvlan_network_is_a_lan_exposure`
- `An_external_network_that_does_not_exist_refuses_the_plan`
- `An_external_network_name_that_is_not_unique_refuses_the_plan`
- `Unreadable_networks_refuse_the_plan`
- `A_pre_existing_non_external_network_is_decided_by_its_live_driver`
- `Network_create_with_macvlan_carries_a_lan_exposure`
- `Network_create_with_bridge_carries_none`
- `Network_connect_to_an_ipvlan_network_carries_a_lan_exposure_for_the_container`

**Gateway / integration (`ComposeImpactExposureTests`, `NetworkToolsGatewayTests`, `WebhookMutationTests`)**

- `Deploy_compose_with_macvlan_ends_in_confirm_under_an_admin_autonomous_policy`
- `An_approval_for_network_id_x_does_not_cover_a_network_recreated_under_the_same_name` (binding: `network.connect`
  binds the resolved network id into the `ApprovalBinding`; a network removed and re-created as macvlan under the same
  name has another id, so the approval does not apply and the call is decided again)
- `Create_network_macvlan_over_mcp_ends_in_confirm_under_an_admin_autonomous_policy`
- `Create_network_refuses_a_driver_changed_after_the_decision` (`EnsureLanDecided`)
- `Connect_uses_the_resolved_network_id_not_the_callers_name`
- `A_network_recreated_under_the_same_name_after_the_decision_fails_the_connect`
- `A_key_owned_compose_webhook_with_macvlan_is_refused`
- `A_human_owned_webhook_with_macvlan_runs_is_audited_and_notifies`
- `The_networks_page_creates_macvlan_without_confirmation_and_notifies` (bUnit: hint line shown)
- `Step_4_denies_an_external_network_recreated_as_macvlan_after_planning`
- `The_re_check_stops_a_container_attached_to_an_undecided_lan_network_under_the_agent_rule_set`
- `The_re_check_counts_unreadable_networks_as_undecided_host`
- `A_lan_exposure_value_from_the_env_appears_in_no_sink`
- `MutationGatewayCoverageTests`, `AuditPathMatrixTests`: unchanged rows still pass.

**Real hardware** `[braucht Nutzer/Hardware]`

1. On a test host with a NIC: `docker network create -d macvlan -o parent=<nic> --subnet … lan`; deploy a compose
   project with `networks: { lan: { external: true } }` through the agent under an Admin-autonomous preset: the
   approval card shows the LAN reason; after approval the container answers on its LAN address from another machine.
2. Same project from the compose editor as a person: no dialog, an `exposure_change` notification arrives.
3. Agent `connect_container_to_network` to `lan`: Confirm with the reason.
4. Compose v2 on the host fills `services.<s>.networks.default` in `docker compose config --format json` for a service
   without `networks:` (the model rule covers it either way; this checks the config rule).

## 5. Decisions (maintainer, 16 November 2026: all as recommended)

- **D1 — Class of `LanNetwork`.** Options: (a) `host`; (b) `published`, literally "like a published port"; (c) a new
  class `lan` added to the default `ExposureConfirmClasses`. **Recommended: (a) `host`.** The decision of 2026-11-16
  says the agent needs a confirmation; a published port (class `published`) is *not* confirmed by default (0006 D3,
  `GuardrailPolicy.cs:93`), so (b) would not deliver it. (c) would not reach policies stored with an explicit
  `ExposureConfirmClasses` list (only `null` takes the default), so on those it would be silently unconfirmed — fail
  open — and it changes every policy's `VersionHash` again. `host` is confirmed by default and fits: the container is
  outside the host's firewall.
- **D2 — Drivers Whiskers does not know.** Options: (a) fail closed: every driver outside `bridge`, `overlay`, `host`,
  `none`, `null` counts as LAN; (b) only `macvlan` and `ipvlan` count, anything else is no exposure; (c) unknown drivers
  refuse the plan / the call outright. **Recommended: (a).** Third-party drivers (plugins, Windows `transparent` /
  `l2bridge`) can do exactly what macvlan does, and their names can be anything; (a) costs one confirmation for the
  agent, (b) misses them silently, (c) blocks persons, who are not otherwise blocked (PC-D1).
- **D3 — Internal macvlan/ipvlan (no `parent`, or `internal: true`).** Options: (a) still `LanNetwork`; (b) no
  exposure. **Recommended: (a).** Simpler, and the network can be re-created with a parent under the same name; the
  shown detail says "internal". Note: Whiskers' own create path sends no options (`NetworkOperations.cs:71-75`), so a
  macvlan created by Whiskers is such an internal network today; it still counts.
- **D4 — Bridge networks that reach the LAN.** A `bridge` network with `com.docker.network.bridge.gateway_mode_ipv4/6
  = nat-unprotected` (or `routed`), or with `com.docker.network.bridge.name` naming an existing bridge that holds a
  physical NIC, also makes containers reachable from the LAN. Options: (a) out of scope, named in the README's
  remaining risks; (b) count these `driver_opts` as `LanNetwork` too (model, config and live `Options`; `NetworkInfo`
  gains `Options`). **Recommended: (a) for this contract**, with (b) as its own follow-up: the decision of 2026-11-16 is
  about macvlan/ipvlan, and whether a named bridge holds a NIC needs a host read this contract does not otherwise
  make.
- **D5 — Step 4 re-reads the networks.** Options: (a) yes, Deny under the agent rule set on a new LAN hit before `up`;
  (b) rely on the re-check after `up` only. **Recommended: (a)**, consistent with image labels (Amendments 4–6): a
  container that is never started is better than one stopped after it was reachable.
- **D6 — INV-62 status until built.** Options: (a) stays ✅, README remaining risks name macvlan/ipvlan now; (b) ⚠️
  from acceptance until built. **Recommended: (a).** The statement as written does not claim LAN networks; (b) would
  reopen the release gate for a gap that was already deferred in 0017 A3. Either way the INV-62 row is edited only
  when built (release-gate rule R5): in the implementing PR, with its tests existing; no planned test stands on a ✅
  row.
- **D7 — Driver list configurable?** Options: (a) fixed list in code; (b) a `GuardrailPolicy` key. **Recommended:
  (a).** A knob here could only widen `NotLan` (fail open); `ExposureConfirmClasses` already lets an operator turn the
  confirmation off.

## 6. Out of scope

- `host.exec` / terminals running `docker network create|connect` themselves (INV-24/59 decide them as host commands).
- Bridge networks that reach the LAN by options (D4).
- `docker network disconnect` (it narrows exposure) and `network.remove`.
- Containers started outside Whiskers, and the AutoPatch / `container.update` recreation of a container that is
  already on a LAN network (it keeps the networks it had; contract 0013 D14 already refuses pinned containers).
- Swarm-scoped networks seen from a worker node (Whiskers manages standalone engines).
- Kubernetes (CNI plugins such as Multus/macvlan).

## 7. Documentation

- `docs/v1/SECURITY_INVARIANTS.md`: INV-62 as in § 3, edited in the implementing PR only (R5).
- `docs/v1/PRODUCT_CONTRACT.md` § 7: rows M-19, M-21, U-13 as in § 3.
- README "remaining risks": macvlan/ipvlan until built (D6); after it, D4's bridge options.
- Guardrails README: the new kind and its class.
- CHANGELOG (behaviour change): an agent's deployment or network operation that puts a container on a macvlan/ipvlan
  network (or a network with an unknown driver) now waits for confirmation even under an Admin-autonomous policy; a
  plan whose external network is missing or whose networks cannot be read is refused.

## 8. Plan triage

- [Claude-prüfbar] § 1.1–1.7, every test of § 4 except the hardware list, docs of § 7.
- [braucht Nutzer/Hardware] the four checks at the end of § 4.

## 9. Line references

As of `ccd5137e`: `ComposeExposure.cs:7-29`, `:83-89`, `:419-497`, `:518-577`; `ComposeModelExposures.cs:20-54`,
`:70-80`; `ComposeConfigEffects.cs:141-150`; `ComposePlanner.cs:97`, `:304`; `ComposeRunner.cs:448`;
`MutationTargets.cs:146-149`, `:530-582`; `MutationPolicy.cs:127-155`; `ExposureChangeRule.cs:31-55`;
`GuardrailPolicy.cs:93`; `NetworkTools.cs:41-71`, `:100-115`; `NetworkOperations.cs:68-77`; `Networks.razor:106-110`,
`:172-189`; `CapabilityCatalog.cs:246-248`; `IDockerService.cs:137-142`.

## 10. Amendment text for contract 0006 (appended when built)

> Amended by contract 0021 (1.1.0): the closed list of exposure kinds (Amendment 8, as extended by contract 0017) gains
> `LanNetwork` (class `host`, key the network's name on the host, value the driver): a service on a network whose
> driver is `macvlan`, `ipvlan` or any driver other than `bridge`, `overlay`, `host`, `none` and `null` — declared in
> the files (the implicit `default` network included), resolved by `docker compose config`, or read live for an
> external or pre-existing network (missing, ambiguous or unreadable refuses the plan). It is read again at step 4 and
> in the re-check (`NetworkSettings.Networks` by network id; unreadable → undecided `host`). `network.create` with
> such a driver and `network.connect` to such a network carry the same exposure in the resolved target, and
> `network.connect` runs on the resolved network id.
