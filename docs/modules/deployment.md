# Module: deployment

App deployment + the app store — the final SAP Phase-1 module. The `/deploy` page (deploy from a form or a
docker-compose file, both as a compose project through the compose runner, contract 0013) and the `/apps` page
(built-in templates + multi-registry image search across Docker
Hub / GHCR / Harbor).

| | |
|---|---|
| **Id** | `deployment` |
| **Enabled by default** | yes |
| **Toggle** | `Features:deployment:Enabled` (env `Features__deployment__Enabled=false`) — restart required |
| **Depends on** | — (the AppStore page uses Core no-op defaults) |
| **Nav** | `deploy` — "Deploy", `apps` — "App store" (group *Deployment*) |
| **MCP tools** | — of its own; `deploy_app`/`deploy_compose` stay in the Core, mixed `ContainerTools` |
| **Services** | `ITemplateService` + `IImageSearchService` (+ 3 providers); the form's mapper `DeploymentCompose` and `DeploymentPathPlanner` need no registration |

When **disabled**: the `deploy` + `apps` nav entries disappear and both pages show a "module disabled" notice.
`deploy_app` (in Core `ContainerTools`) is refused with "Deployment refused: the Deployment module is disabled (set
Features:deployment:Enabled=true …)" — by the tool and by the gateway's target resolution, on every surface
(`DeploymentModuleGate`). `deploy_compose` and the `/compose` editor stay Core and keep working. Template lookups /
image searches return empty.

**Exposure and approvals.** Every deploy path is decided by the compose runner's exposure check (contract 0006,
INV-62): published ports, host namespaces, route labels and LAN networks (macvlan, ipvlan and every driver other than
`bridge`, `host`, `none`, `overlay`; contract 0021) need a confirmation for an agent or key, and send a notification
for a person. A deploy that touches a protected container raises an approval; a signed-in Admin can approve it in the
dialog (contract 0019, wave 2). Git-deploy apps have an owner; a handed-over app is held until someone switches it on
(contract 0020).

**`compose` stays Core:** the `/compose` editor uses only Docker + host-command services, so it isn't part of
this module. `ImageUpdate`/`AutoUpdate` are also out of scope (separate concerns).

No settings section (deploy + app-store are driven from their pages; image-search registries via
`ImageSearch` config, bound in the module).

Code: [`src/Whiskers/Modules/Deployment/`](../../src/Whiskers/Modules/Deployment/) · services in
[`Services/Deployment`](../../src/Whiskers/Services/Deployment/),
[`Services/Templates`](../../src/Whiskers/Services/Templates/),
[`Services/ImageSearch`](../../src/Whiskers/Services/ImageSearch/) · deploy tools in Core
[`ContainerTools.cs`](../../src/Whiskers/Mcp/Tools/ContainerTools.cs).
