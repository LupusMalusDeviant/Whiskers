# Modules/GitDeploy

Git-based deployments (F5): the `/git-deploy` page and `IGitDeployService`.

- `GitDeployModule.cs` — `Id = "gitdeploy"`, enabled by default. Registers
  `IGitDeployService` → `GitDeployService` (wins over Core's no-op by last-registration).
  Nav: `git-deploy` ("Git Deploy", group *Deployment*).

**Toggle:** `Features:gitdeploy:Enabled` (`Features__gitdeploy__Enabled=false`), restart-only.

**Soft dependency:** the Webhooks module dispatches the `git-deploy` webhook action through the
Core `IGitDeployService` contract; Core registers `NoopGitDeployService` before the module loop so
a disabled GitDeploy module answers a webhook with a graceful failure (400, not 500).

**DI-safe page guard:** `GitDeployPage.razor` is the thin route wrapper
(`<ModuleGuard ModuleId="gitdeploy"><GitDeployView/></ModuleGuard>`); the interactive logic lives in
`GitDeployView.razor`. Service code: [`../../Services/GitDeploy/`](../../Services/GitDeploy/).
