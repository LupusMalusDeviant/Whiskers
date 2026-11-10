# Services/Deployment

The Deploy page's form and MCP `deploy_app` as **compose projects** (contract 0013, U1): a single-image request
becomes a compose file, and every deployment path then runs a planned compose project through the
[compose runner](../Mutations/) — planned, mounts and exposures decided, images pinned, re-checked. No deployment path
creates a container outside the runner (`DeploymentPathArchitectureTests`).

## Files

| File | Purpose |
|---|---|
| `DeploymentCompose.cs` | Contract 0013, D1: the one pure mapper `DeploymentRequest` → compose file (`ToCompose`), and `deploy_app`'s comma-list arguments → `DeploymentRequest` (`FromToolArguments`, a malformed port or environment entry refuses instead of being dropped). Name = project (normalised) = service = `container_name`; the image literal; `host:container/tcp` ports with an optional bind IP (IPv6 bracketed); short-syntax volumes (a named volume keeps its own name); environment values quoted, `$` written as `$$`; restart policies as before (`on-failure` → `on-failure:5`); `network_mode: bridge` by default, `host`/`none`, any other network external. The file starts with the marker `# whiskers: app.deploy`. The page, the tool and the gateway's target (`MutationTargets`) all call it. |
| `DeploymentPathPlanner.cs` | Contract 0013, D3/D15: wraps `IComposePlanner` for the two paths (`AppDeploy`, `ComposeTab`) and refuses a plan before anything is written when a compose project of that name runs from another directory, when (form and `deploy_app`) `docker-compose.yml` in the directory lacks the marker (another path owns it — use the compose editor), or when a legacy container (no compose labels, created by the old single-container paths) has a name the deployment would use. |

## Wiring

The Deploy page (`/deploy`) is part of the opt-in **Deployment module** ([`../../Modules/Deployment/`](../../Modules/Deployment/),
toggle `Features:deployment:Enabled`), which gates the page and registers the templates and image search. The mapper
and the path planner are static / constructed per call and need no registration: the `deploy_app`/`deploy_compose` MCP
tools live in the Core, mixed `ContainerTools` and use the Core's compose planner and runner. The `/compose` editor is
**not** part of this module (it uses only Docker + host services and stays Core).

## Related

- App templates that pre-fill deployments: [`../Templates/`](../Templates/)
- UI: [`../../Components/Pages/Deploy.razor`](../../Components/Pages/Deploy.razor), [`ComposeEditor.razor`](../../Components/Pages/ComposeEditor.razor), [`AppStore.razor`](../../Components/Pages/AppStore.razor)
- MCP tools: `deploy_app`, `deploy_compose` in [`../../Mcp/Tools/`](../../Mcp/Tools/)
- Compose plan, runner and exposure decision: [`../Mutations/`](../Mutations/) ([contract 0013](../../../../docs/contracts/0013-exposure-changes-complete.md))
