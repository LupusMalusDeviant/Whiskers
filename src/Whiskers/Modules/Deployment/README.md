# Modules/Deployment

App deployment + the app store — the **final** RoadToSAP Phase-1 extraction. The `/deploy` page (a form or a
compose file, both deployed as a compose project through the compose runner, contract 0013) and the `/apps` page
(built-in templates + multi-registry image search). No MCP tools of its own.

- `DeploymentModule.cs` — `Id = "deployment"`, enabled by default. `ConfigureServices` (moved **verbatim** from
  `Program.cs`) registers `ITemplateService`, `ImageSearchSettings`, the three `IImageSearchProvider`s
  (Docker Hub / GHCR / Harbor) and `IImageSearchService`. Nav: `deploy` + `apps` (group *Deployment*).

**Toggle:** `Features:deployment:Enabled` (`Features__deployment__Enabled=false`), restart-only. When off, the
`deploy` + `apps` nav entries disappear and both pages show a "module disabled" notice, and every `app.deploy`
(MCP `deploy_app`, the agent, the pages) is refused with a reason that names the switch
(`Services/Deployment/DeploymentModuleGate.cs`); `deploy_compose` stays Core.

**`compose` stays in Core.** The `/compose` editor (`ComposeEditor.razor`) uses only `IDockerService` +
`IHostCommandExecutor`, not this module's services, so its nav entry stays in `AllInOnePseudoModule`.

**Why two no-ops, and why the deploy MCP tools stay in Core.** `deploy_app`/`deploy_compose` live in
`ContainerTools`, which also holds the core container ops (list/restart/logs/update/…); a tool class can't be
split under the byte-gleich rule, so `ContainerTools` **stays in Core**. Since contract 0013 both tools run the
Core's compose planner and runner and need nothing from this module (the module gates the page, not the tools;
`IDeploymentService` and its no-op are gone). The AppStore page injects `ITemplateService` and
`IImageSearchService`, so Core registers no-op defaults
([`NoopTemplateService`](../../Services/Templates/NoopTemplateService.cs),
[`NoopImageSearchService`](../../Services/ImageSearch/NoopImageSearchService.cs)) before the module loop; the
real services win by last-registration when enabled. The template/search reads return empty.

**Page gating.** `Deploy.razor` and `AppStore.razor` wrap their content inline in
`<ModuleGuard ModuleId="deployment">` (the no-op defaults make the injections safe, and neither
`OnInitializedAsync` calls a mutating operation, so no `*View` split is needed). Service code stays in
[`../../Services/Deployment/`](../../Services/Deployment/) (the form's mapper and the path planner),
[`../../Services/Templates/`](../../Services/Templates/) and
[`../../Services/ImageSearch/`](../../Services/ImageSearch/).

See [`docs/modules/deployment.md`](../../../../docs/modules/deployment.md).
