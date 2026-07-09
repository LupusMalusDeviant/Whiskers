# Services/Deployment

Deploys workloads onto managed hosts, both single containers (from a form: image, ports, env, volumes) and full **Docker Compose** stacks (uploaded or pasted).

## Files

| File | Purpose |
|---|---|
| `IDeploymentService.cs` / `DeploymentService.cs` | Validates and executes deployments; returns a `DeploymentValidationResult` so the UI can surface problems before anything runs. |
| `ComposeFileParser.cs` | Parses Docker Compose YAML into the structures the deployment service and the Compose editor work with. |
| `NoopDeploymentService.cs` | Core default `IDeploymentService` for when the **Deployment module** is off. Keeps the Core, mixed `ContainerTools` resolvable; deploy operations **throw** (never fake a deploy), `ValidateCompose` reports invalid. Real service wins by last-registration when on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **Deployment module** ([`../../Modules/Deployment/`](../../Modules/Deployment/), toggle
`Features:deployment:Enabled`; scoped registration). The `deploy_app`/`deploy_compose` MCP tools live in the
Core, mixed `ContainerTools` (which also has list/restart/logs/…), so it can't move — hence the
`NoopDeploymentService` default for when the module is off. The `/compose` editor is **not** part of this
module (it uses only Docker + host services and stays Core).

## Related

- App templates that pre-fill deployments: [`../Templates/`](../Templates/)
- UI: [`../../Components/Pages/Deploy.razor`](../../Components/Pages/Deploy.razor), [`ComposeEditor.razor`](../../Components/Pages/ComposeEditor.razor), [`AppStore.razor`](../../Components/Pages/AppStore.razor)
- MCP tools: `deploy_app`, `deploy_compose` in [`../../Mcp/Tools/`](../../Mcp/Tools/)
- Container operations: [`../Docker/`](../Docker/)
