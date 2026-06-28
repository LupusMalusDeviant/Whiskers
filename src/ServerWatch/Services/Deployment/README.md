# Services/Deployment

Deploys workloads onto managed hosts, both single containers (from a form: image, ports, env, volumes) and full **Docker Compose** stacks (uploaded or pasted).

## Files

| File | Purpose |
|---|---|
| `IDeploymentService.cs` / `DeploymentService.cs` | Validates and executes deployments; returns a `DeploymentValidationResult` so the UI can surface problems before anything runs. |
| `ComposeFileParser.cs` | Parses Docker Compose YAML into the structures the deployment service and the Compose editor work with. |

## Related

- App templates that pre-fill deployments: [`../Templates/`](../Templates/)
- UI: [`../../Components/Pages/Deploy.razor`](../../Components/Pages/Deploy.razor), [`ComposeEditor.razor`](../../Components/Pages/ComposeEditor.razor), [`AppStore.razor`](../../Components/Pages/AppStore.razor)
- MCP tools: `deploy_app`, `deploy_compose` in [`../../Mcp/Tools/`](../../Mcp/Tools/)
- Container operations: [`../Docker/`](../Docker/)
