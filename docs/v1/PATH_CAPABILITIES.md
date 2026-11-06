# Path rows and their capabilities

Hand-maintained (contract 0008, D6 b). Each row of [`PRODUCT_CONTRACT.md` §7](PRODUCT_CONTRACT.md#7-path-matrix-current-state)
that is not an M row names here the catalog capabilities (`CapabilityCatalog`) its path performs; an M row names its
MCP tool in §7 itself. A row that performs no capability says why, after a dash. The release gate
(`ReleaseGateTests`) reads both tables below:

- every capability of the catalog is named by a §7 row (M rows through their tool, the others here), is composed by
  another capability (`MutationCompositions`), or is listed under *Capabilities without a path* (R13);
- every capability named here exists, and every §7 row that is not an M row has exactly one line here (R13, R14).

The joined view is generated into [`CAPABILITY_INVENTORY.md`](CAPABILITY_INVENTORY.md).

## Rows and capabilities

| Row | Capabilities |
|---|---|
| A-01 | — the agent's tool calls are the M rows' capabilities, decided per call by the gateway |
| A-02 | — the agent's tool calls are the M rows' capabilities, decided per call by the gateway |
| A-03 | — the trigger's tool calls are the M rows' capabilities, decided per call by the gateway |
| A-04 | — dead code (C-20), it performs nothing |
| S-01 | host.exec, host.prune, db.backup, db.backup_delete, selfbackup.create, selfbackup.prune, volume.backup, volume.backup_delete, container.restart |
| S-02 | container.update, container.rollback |
| S-03 | autopatch.run |
| S-04 | compose.deploy, container.restart, container.update, gitdeploy.deploy |
| S-05 | gitdeploy.deploy |
| S-06 | — monitors change no workload, file or setting; the CVE scan's host caches are a designed exemption (R1-48) |
| S-07 | vpn.bootstrap |
| S-08 | — the audit chain's own retention, outside the gateway by design |
| S-09 | — designed exemption from the gateway (R1-48): the pre-upgrade self-backup of the start-up guard |
| S-10 | — host access outside the app (R1-48): the CLI restore |
| S-11 | — host access outside the app: the CLI account recovery |
| S-12 | — the recovery-code redemption, an anonymous page outside the gateway (R1-49) |
| S-13 | — designed exemption from the gateway (INV-38): the first administrator of the setup wizard |
| S-14 | — designed exemption from the gateway (INV-38): the identity binding at sign-in |
| S-15 | — host access outside the app: the CLI binding confirmation |
| S-16 | endpointcheck.run |
| T-01 | terminal.container_open |
| T-02 | terminal.container_open |
| T-03 | terminal.host_open |
| U-01 | container.start, container.stop, container.restart, workload.start, workload.stop, workload.restart |
| U-02 | container.remove |
| U-03 | container.update |
| U-04 | container.rollback |
| U-05 | container.start, container.stop, container.restart, container.remove |
| U-06 | container.env_file_save |
| U-07 | db.query |
| U-08 | container.exec, db.query |
| U-09 | db.backup |
| U-10 | app.deploy, compose.deploy |
| U-11 | app.deploy |
| U-12 | compose.deploy |
| U-13 | network.create, network.remove |
| U-14 | firewall.set_status |
| U-15 | firewall.rule_add, firewall.rule_remove |
| U-16 | nginx.update, nginx.site_enable, nginx.site_disable |
| U-17 | nginx.reload |
| U-18 | systemd.manage |
| U-19 | ssl.renew |
| U-20 | server.save, server.remove |
| U-21 | server.onboard |
| U-22 | checks.pause, checks.resume |
| U-23 | cloud.power_on, cloud.shutdown, cloud.reboot, cloud.hard_reset, cloud.snapshot_create |
| U-24 | volume.backup, volume.restore, volume.backup_delete |
| U-25 | selfbackup.create, selfbackup.delete, selfbackup.restore |
| U-26 | scheduler.create, scheduler.update, scheduler.delete |
| U-27 | scheduler.run |
| U-28 | webhook.save, webhook.rotate_secret, webhook.set_enabled, webhook.delete |
| U-29 | gitdeploy.save, gitdeploy.delete, gitdeploy.deploy |
| U-30 | logalert.create, logalert.delete, logalert.toggle |
| U-31 | aitrigger.save |
| U-32 | guardrails.save, guardrails.kill_switch_off |
| U-33 | — an approval decides another operation; approving and rejecting are the approval store's own records, not catalog capabilities |
| U-34 | config.save, config.whitelist, config.roles, config.mcp_permissions, config.agent, mcp.key_create, mcp.key_delete, vault.secret_set, vault.secret_delete, registry.save, registry.delete, automation.owner_set |
| U-35 | account.password_change, account.create, account.password_reset, account.delete |
| U-36 | identity.binding_confirm, identity.binding_remove |
| U-37 | mtls.client_migrate, mtls.shared_client_retire |
| U-38 | endpointcheck.save, endpointcheck.delete, endpointcheck.run |

## Capabilities without a path

A capability of the catalog that no path performs today. Each line says why; a capability that gains a caller moves
to the row of that path.

| Capability | Why |
|---|---|
| autoupdate.policy_set | declared on `IAutoUpdateService.SetPolicyAsync`, which no page, tool or job calls today |
