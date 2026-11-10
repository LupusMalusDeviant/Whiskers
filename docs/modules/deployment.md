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
| **Nav** | `deploy` — "Bereitstellen", `apps` — "App Store" (group *Deployment*) |
| **MCP tools** | — of its own; `deploy_app`/`deploy_compose` stay in the Core, mixed `ContainerTools` |
| **Services** | `ITemplateService` + `IImageSearchService` (+ 3 providers); the form's mapper `DeploymentCompose` and `DeploymentPathPlanner` need no registration |

When **disabled**: the `deploy` + `apps` nav entries disappear and both pages show a "module disabled" notice.
The `deploy_app`/`deploy_compose` MCP tools (in Core `ContainerTools`) keep working — they run the Core's compose
planner and runner (contract 0013) — and template lookups / image searches return empty.

**`compose` stays Core:** the `/compose` editor uses only Docker + host-command services, so it isn't part of
this module. `ImageUpdate`/`AutoUpdate` are also out of scope (separate concerns).

No settings section (deploy + app-store are driven from their pages; image-search registries via
`ImageSearch` config, bound in the module).

Code: [`src/Whiskers/Modules/Deployment/`](../../src/Whiskers/Modules/Deployment/) · services in
[`Services/Deployment`](../../src/Whiskers/Services/Deployment/),
[`Services/Templates`](../../src/Whiskers/Services/Templates/),
[`Services/ImageSearch`](../../src/Whiskers/Services/ImageSearch/) · deploy tools in Core
[`ContainerTools.cs`](../../src/Whiskers/Mcp/Tools/ContainerTools.cs).
