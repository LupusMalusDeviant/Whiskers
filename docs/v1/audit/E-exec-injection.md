# Audit E — Command execution, injection surfaces, remote-access transports

Scope: `HostCommandExecutor` + callers, `ShellUtils`, `Services/Docker/**`, `Onboarding/**`,
`Certificates/**`, `Deployment` + `GitDeploy`, `Workloads/Kubernetes`, `Database`, `Terminal` + Hubs,
firewall/nginx/systemd/SSL, `Dns`, `Registries`/`ImageSearch`, `Notifications`/`Webhooks`, `Cve/TrivyScanner`,
`Vpn`, `docs/ssh-haertung.md`, `docs/adr/0002`.

READ-ONLY review. No repository files were modified.

---

## Threat-model note (sets severity throughout)

Whiskers is, by design, a root control plane: the host-shell plane runs every command as **host root** —
either via `nsenter -t 1` on the local host (`HostCommandExecutor.ExecuteLocalAsync`, HostCommandExecutor.cs:76)
or via a **one-shot `Privileged=true, PidMode="host"` nsenter container over the mTLS Docker connection**
(`HostShellOperations.RunHostShellAsync`, HostShellOperations.cs:74-80). So the question is almost never "can
code run on the host" (the product's whole purpose is to run code on the host) but **"can a caller below the
intended privilege level, or an attacker-supplied value, reach that plane."** Findings are graded on that basis.

Positive control to keep in mind while reading: the MCP permission boundary is enforced per call
(`McpPermissionCheck.CheckAccess`, McpPermissionCheck.cs:18) and the in-process AI agent's rights are capped at
its principal level (`AgentToolInvoker` sets `McpLevelClaim`, AgentToolInvoker.cs:228; ceiling enforced), so the
agent cannot call the `Admin` tools below. `execute_command`, `deploy_app`, `deploy_compose`,
`renew_mtls_certificate` are all `Admin` (McpPermission.cs:108-113,164).

---

## Findings

### E-01 — `ShellUtils.Quote` stops shell-metachar injection but NOT argument/option injection; git branch is unvalidated
- **Severity:** Medium · **Category:** Security · **v1-blocker:** No (admin-gated) · *needs verification of git's exact parse*
- **Evidence:**
  - `ShellUtils.Quote` = POSIX single-quote only (ShellUtils.cs:14).
  - `GitDeployCommands.CloneOrUpdateCommand` (GitDeployCommands.cs:36-45):
    `... {env}git fetch origin {br} && git checkout -q {br} && git reset --hard origin/{ShellUtils.Quote(branch)}; else ... git clone --branch {br} --single-branch {repo} repo`
    where `br = ShellUtils.Quote(branch)`.
  - `branch` is never charset-validated. `SaveAppAsync` validates the URL (`https://` only), compose path
    (no leading `/`, no `..`) and app id (hex) — but **not the branch** (GitDeployService.cs:69-74). Default `"main"`.
- **Attack path:** Single-quoting is a *shell*-level defence. After the shell strips the quotes, `git` still
  receives the literal token; a branch value beginning with `-`/`--` (e.g. `--upload-pack=…`, `--exec=…`) is
  parsed by `git fetch origin <token>` as an **option**, not a ref → argument injection into git, which has
  historically meant local command execution. The repo content itself is also built on the host
  (`docker compose build`, GitDeployCommands.cs:52) = arbitrary code execution by design when deploying an
  untrusted repo.
- **Reachability:** `branch` is set only through the Admin-gated Git-deploy UI (GitDeployView.razor:200,217 require
  `AppRole.Admin`). So an Admin attacking the host they already own — low practical gain — but it is the clearest
  instance of the systemic issue and the fix is cheap.
- **Recommendation:** (1) Validate branch against a strict ref charset (no leading `-`, forbid `..`), mirroring
  `SystemdService.SafeServiceNameRegex`. (2) Where a value is a positional argument, insert `--` before it
  (`git fetch origin -- <ref>` is not valid, so validate instead) — the general rule the codebase should adopt:
  *quoting is not argument-safety*. Add a code-building test asserting a `-`-leading value is rejected.

### E-02 — SSH user/host/port are never validated → `ssh` option injection (ProxyCommand) and host-key-file selection
- **Severity:** Medium · **Category:** Security · **v1-blocker:** No (admin-gated)
- **Evidence:** `HostCommandExecutor.ExecuteSshAsync` builds `args.Add($"{server.SshUser}@{server.SshHost}")`
  (HostCommandExecutor.cs:141) and `TerminalSession.StartSsh`/`StartSshDockerExec` do the same via
  `SshBaseArgs` (TerminalSession.cs:41-61). `SshTunnelManager.StartSshProcess` likewise (SshTunnelManager.cs:124).
  `SshUser`/`SshHost` are plain nullable strings on the model (ServerConfig.cs:59-61) with **no charset check**
  anywhere (grep confirms only null checks).
- **Attack path:** `SshUser = "-oProxyCommand=<cmd>"` makes the concatenated token `-oProxyCommand=<cmd>@host`
  begin with `-`; `ssh` parses it as an option → `ProxyCommand` runs an arbitrary command on the **Whiskers host**.
  Because the token is an argv element (not shell-interpolated) this bypasses the "single argv element" safety
  the file relies on. Values are Admin-set (Servers.razor is `AppRole.Admin`-gated), so again admin-on-own-host,
  but it defeats the stated transport hardening.
- **Recommendation:** Validate `SshUser` (POSIX user name charset, no leading `-`), `SshHost`
  (hostname/IP), and keep `SshPort` as the int it is. Consider inserting the user via `-l <user>` and the host
  as a bare positional after `--`.

### E-03 — `nginx` site-name regex allows `..` and a leading `-` (unlike the other name validators)
- **Severity:** Low · **Category:** Security · **v1-blocker:** No
- **Evidence:** `NginxService.SafeSiteNameRegex = ^[a-zA-Z0-9._-]+$` (NginxService.cs:20). Contrast
  `SystemdService` (`^[a-zA-Z0-9][a-zA-Z0-9._@-]*$`, leading-alnum, SystemdService.cs:22),
  `SslCertService` (`^[a-zA-Z0-9][...]*$`, SslCertService.cs:28) and
  `McpInputValidation.SafeProjectNameRegex` (leading-alnum + explicit `!name.Contains("..")`, McpInputValidation.cs:14).
  Site name flows into `/etc/nginx/sites-available/{siteName}` for `cat`, `tee`, `ln -sf`, `rm -f`
  (NginxService.cs:81,98,143,154,172).
- **Attack path:** `siteName = ".."` → paths resolve to `/etc/nginx` (a directory); `/` is blocked so deeper
  traversal is not possible and option injection is blocked because the value is always embedded behind a
  leading `/…/` path segment. Practical impact is limited (blind, one level), hence Low — but the inconsistency
  is a latent bug and the comment claims `..` is excluded ("only safe site names") when it is not.
- **Recommendation:** Align the regex with the others: `^[a-zA-Z0-9][a-zA-Z0-9._-]*$` and reject `..`.

### E-04 — `execute_query` (Write level) grants full DB-superuser SQL incl. DROP and `COPY … TO PROGRAM` (RCE in DB container)
- **Severity:** Medium · **Category:** Security / Architecture · **v1-blocker:** No (intended, but scope worth a decision)
- **Evidence:** `DatabaseTools.ExecuteQuery` is `Write` (DatabaseTools.cs:111; McpPermission.cs:124). Credentials
  are harvested from the container's own env and are typically the superuser
  (`POSTGRES_USER`/`MYSQL_ROOT_PASSWORD`, DatabaseDetector.cs:35-53). `BuildQueryCommand` passes the query
  verbatim as `psql -c <query>` / `mysql -e <query>` / `mongosh --eval <query>` (DatabaseService.cs:220-228).
- **Attack path:** A **Write**-level (Operator) MCP key — not just Admin — can `DROP DATABASE`, `DELETE`, or in
  PostgreSQL `COPY … TO PROGRAM '…'` / in MySQL arbitrary admin, i.e. destroy data and gain code execution
  inside the DB container. The tool description ("Execute a SQL query") does not convey that it is
  unrestricted superuser access. No read-only mode, no DROP guard.
- **Recommendation:** Decide explicitly whether destructive SQL belongs at Write or Admin. At minimum document
  it, and consider an opt-in "allow destructive" flag or a read-only transaction default. (The caller's
  Audit-A/architecture reviewer should weigh in on the permission-level placement.)

### E-05 — Compose / app deploy accepts arbitrary `privileged`, host bind-mounts and docker.sock mount = host takeover
- **Severity:** Medium · **Category:** Security · **v1-blocker:** No (correctly `Admin`-gated; documented)
- **Evidence:** `ContainerTools.DeployCompose` writes caller-supplied `composeContent` to
  `/opt/deployments/{projectName}/docker-compose.yml` (base64, project name validated via
  `IsSafeProjectName`) and runs `docker compose up -d` (ContainerTools.cs:490-534). `DeployApp` accepts arbitrary
  `volumes` (`hostPath:containerPath`) with no path restriction (ContainerTools.cs:333-339).
- **Attack path:** A compose file mounting `/:/host` or `/var/run/docker.sock`, or `privileged: true`, yields
  de-facto host root. This is inherent to a deploy tool. **Positive:** the project already recognises this —
  both tools are `Admin` (McpPermission.cs:108-110) with the explicit comment "a container they create can mount
  the host root = de-facto root on the host. Keep them above the write boundary." Project name is path-traversal
  checked (`IsSafeProjectName`, no `..`). So this is noted as *intended and correctly bounded*, not a defect.
- **Recommendation:** None required for v1 beyond what exists; optionally a compose linter that warns on
  `privileged`/`docker.sock`/host-root mounts for operator visibility.

### E-06 — Single shared mTLS client credential + fixed `--allow-cn=serverwatch-client` across the whole fleet
- **Severity:** Medium · **Category:** Security / Architecture · **v1-blocker:** No · *needs verification of key storage mode*
- **Evidence:** Every onboarded host's ghostunnel is configured with `--allow-cn=serverwatch-client`
  (OnboardingCommands.cs:109). The client cert/key are a single shared pair under the data dir
  (`client.crt`/`client.key`, OnboardingService.cs:198-200; `BuildMtlsCredentials` loads them,
  DockerConnectionManager.cs:309).
- **Attack path:** The one client key held on the Whiskers host authenticates to **all** hosts. If that key
  leaks (disk compromise of the control plane), the whole fleet's privileged Docker proxy is open. There is no
  per-host client identity to revoke. This is an acceptable control-plane trust model, but it concentrates fleet
  compromise into one file.
- **Recommendation:** Document the blast radius; consider per-host client certs (unique CN) so one leak/rotation
  is scoped. Verify the client key file mode/ownership on disk.
- **Positive controls here:** server-cert validation is solid — `ValidateMtlsServerCert` requires BOTH a chain to
  the custom CA AND a hostname match, and **fails closed on an empty expected host** (DockerConnectionManager.cs:334-349,
  comment NIED-13). CSR/issued-cert validation (`MtlsCertificateValidator`) pins exact SANs, rejects CA basic-
  constraints, and binds the issued cert to the CSR key (MtlsCertificateValidator.cs:33-90). The renewal flow has a
  host-side dead-man rollback so a bad cert self-reverts (MtlsCertificateCommands.cs:90-116). Revocation is
  `NoCheck` (DockerConnectionManager.cs:340) — standard for an offline private CA, noted not flagged.

### E-07 — Inbound webhook executes host/Docker actions; authenticated by HMAC but endpoint is anonymous and `DeployCompose` runs arbitrary dir
- **Severity:** Low · **Category:** Security · **v1-blocker:** No
- **Evidence:** `POST /api/webhooks/{webhookId}` is `.AllowAnonymous()` (WhiskersPipelineExtensions.cs:293-302).
  `WebhookService.TriggerAsync` requires a non-empty secret and a constant-time-compared HMAC over the raw body
  and rejects secret-less rows fail-closed (WebhookService.cs:96-112) — good. Actions: restart/rebuild/deploy/
  git-deploy. `DeployCompose` requires an absolute path and single-quotes it (WebhookService.cs:182-195).
- **Attack path:** Security rests entirely on the webhook secret (256-bit, server-generated, WebhookService.cs:216)
  and the `webhookId` being unguessable. No rate-limiting on the anonymous endpoint (brute-force of the HMAC is
  infeasible, but the endpoint will happily run DB/`docker compose` work per valid call). `TargetId` for
  `restart`/`rebuild` is matched by `c.Id.StartsWith(webhook.TargetId)` (WebhookService.cs:158,168) — a short
  prefix could match an unintended container, though the webhook is admin-created so target is fixed.
- **Recommendation:** Keep as is for v1; optionally add basic rate-limiting / lockout and replace the
  `StartsWith` prefix match with `McpInputValidation.Resolve` (which already rejects ambiguous prefixes).

### E-08 — VPN provider args are concatenated into a single `Arguments` string (space/flag injection from settings)
- **Severity:** Low · **Category:** Security · **v1-blocker:** No (admin-config)
- **Evidence:** `VpnProcessRunner.RunAsync` uses `ProcessStartInfo.Arguments = arguments` (VpnProcessRunner.cs:21,77)
  — a single string that .NET re-splits. Callers build it by interpolation:
  `args += $" --hostname {_settings.Hostname}"` / `--management-url {nb.ManagementUrl}` (NetbirdVpnProvider.cs:50-51),
  `up --hostname={hostname} --login-server={ts.LoginServer}` (TailscaleVpnProvider.cs:67-69).
- **Attack path:** A `Hostname`/`LoginServer` containing a space injects extra CLI flags into `tailscale`/
  `netbird`, which run as the host-root Whiskers process. `--login-server`/`--management-url` are themselves
  attacker-pointable endpoints (minor SSRF-ish). All values are Admin settings, so impact is low.
- **Recommendation:** Use `ArgumentList` (as every other `Process.Start` site in the repo does) instead of a
  concatenated `Arguments` string; validate hostname charset. (Secrets are already passed via env, not argv —
  good, VpnProcessRunner.cs:28-30 / provider comments.)

### E-09 — Kubeconfig loaded from vault is parsed into a live client → exec-credential plugins could run on the host
- **Severity:** Low · **Category:** Security · **v1-blocker:** No · *needs verification that exec plugins are reachable in this client build*
- **Evidence:** `KubernetesClientCache.Build` reads the kubeconfig YAML from the vault and
  `KubernetesClientConfiguration.BuildConfigFromConfigObject(...)` builds the client
  (KubernetesClientCache.cs:60-67). Kubeconfig is admin-supplied (stored encrypted in vault; vault required).
- **Attack path:** A kubeconfig `users[].user.exec` block tells client-go-style libraries to run a local binary
  for credentials. If the C# `k8s` client honours exec plugins, loading a malicious kubeconfig would execute a
  binary on the Whiskers host. Needs verification whether the `KubernetesClient` NuGet honours `exec` auth and
  whether the binary path is attacker-controlled. Admin-supplied + vault-gated, so Low.
- **Recommendation:** Verify exec-plugin behaviour of the client library; if honoured, reject kubeconfigs
  containing `exec` auth, or run the kube client with exec disabled.

---

## Surfaces checked and found sound (positive controls)

- **`HostCommandExecutor` core design** — commands are handed to `sh -c` as a **single argv element**, not a
  concatenated string, so .NET does no word-splitting and the intended pipes/quoting are the caller's only
  (HostCommandExecutor.cs:76-82, 139-141). Output is capped per stream with explicit truncation signalling to
  callers (`ReadCappedAsync`, HostCommandExecutor.cs:230-266). Timeouts default to 30s and the whole process
  **tree** is killed on timeout/cancel (HostCommandExecutor.cs:206-226) — no unbounded execution. SSH passwords
  go via `SSHPASS` env, never argv (HostCommandExecutor.cs:162-166). `RunHostShellAsync` has a per-command
  timeout and force-removes the helper container in `finally`, plus a periodic sweep of leaked helpers
  (HostShellOperations.cs:88-118,127-152).
- **SSH host-key verification (ADR-0002 / HOCH-11)** — a single shared policy
  (`SshHostKeyPolicy.Options`, SshHostKeyPolicy.cs) applied at all three ssh spawn sites (tunnel, executor,
  terminal): `StrictHostKeyChecking=accept-new` (TOFU, not disabled) with a persistent `UserKnownHostsFile`
  under the data dir. `BatchMode=yes`, `ExitOnForwardFailure`, keepalives on the tunnel (SshTunnelManager.cs:118-131).
  TOFU (not full pre-seeded verification) is an accepted, documented trade-off in `docs/adr/0002` — this matches.
- **Name/argument validators that DO prevent option injection** — systemd service (SystemdService.cs:22,124),
  SSL cert (SslCertService.cs:28,154), firewall port/proto/action/CIDR allow-lists (FirewallService.cs:389-407),
  compose project name incl. explicit `..` rejection (McpInputValidation.cs:14-18).
- **Base64 transport for file content** — nginx config, `.env`, compose files, onboarding files, mTLS scripts,
  git credentials and the scrape-config python all travel base64-encoded so arbitrary content never meets shell
  quoting (NginxService.cs:97-101, ContainerTools.cs:460-462/518-523, OnboardingCommands.cs:25-57,
  MtlsCertificateCommands.cs:9-10/21-25, GitDeployCommands.cs:16-32). Secrets (DB passwords, tokens, VPN keys)
  are passed via env vars, not argv, so they stay out of the process list (DatabaseService.cs:176-245,
  GitDeployCommands.cs:8, VpnProcessRunner.cs:28).
- **Git repo URL hardening** — only `https://` accepted; `file://`, `ssh://`, `git://` and local paths are
  rejected (GitDeployService.cs:70-71). Compose path must be repo-relative, no leading `/`, no `..`
  (GitDeployService.cs:72-74). App id is hex-validated before ever touching a path (GitDeployService.cs:41).
- **MCP permission boundary** — web-cookie callers are mapped to their real `AppRole`→level (the comment notes a
  prior bug where any cookie = full access; now fixed, McpPermissionCheck.cs:24-35). Unlisted tools fail closed
  to `Admin` (McpPermission.cs:62-69). Agent principal ceiling caps the in-process agent at its level so it
  cannot reach `execute_command`/deploy/`renew_mtls` (AgentToolInvoker.cs:228, McpPermission.cs:108-164).
  `execute_command` is `Admin`, audited (redacted) with exit code and serverId (ServerTools.cs:115-151).
- **SSRF surfaces (registries / image search / notifications / DNS)** — all target endpoints are **admin
  configuration**, not per-request user URLs: Docker Hub/GHCR hosts are constants (DockerHubSearchProvider.cs:12-13,
  GhcrSearchProvider.cs:65-95), Harbor base URL is normalised to authority-only from settings
  (HarborSearchProvider.cs:26-28), outbound notification webhook URL is a single settings value
  (WebhookNotificationService.cs:29-30,65), DNS is API-based with zone allow-listing and name validation
  (DnsRecordService.cs:137-167). No user-supplied URL is fetched server-side, so classic SSRF (metadata
  endpoint, internal pivot) is not exposed to a low-privilege caller. The outbound VPN `--login-server`/
  `--management-url` (E-08) are the only partial exception, and admin-set.
- **Terminal privilege** — host root shell (`nsenter`) and SSH server shells require `AppRole.Admin`
  (ServerTerminalView.razor:48-52); container exec terminal requires `AppRole.Operator`
  (TerminalView.razor:52-56); hub is `[Authorize]` (ContainerHub.cs:7) and exposes no shell-spawn method.
  mTLS-only servers correctly refuse a web terminal rather than silently mis-wiring (TerminalSessionManager.cs:80-82).
- **Cross-server targeting** — `serverId` resolves through `ServerConfigService.GetServer` (exact id, unique);
  a request for server X cannot be steered to Y. The one nuance: a **null** `serverId` falls back to `"local"`
  across DB/backup/webhook/scheduler paths (`?? "local"`, e.g. DatabaseService.cs:33,202). That targets the
  local host rather than cross-contaminating another server — benign, but means a DB tool invoked without a
  serverId runs `docker exec` on the local daemon even if the container lives remotely (a usability edge, not a
  security pivot).
- **Unsafe deserialization** — no `BinaryFormatter`/`JavaScriptSerializer`/`TypeNameHandling` found in scope;
  config stores are `System.Text.Json`; kubeconfig YAML is the only deserialization of externally-authored data
  (see E-09).

---

## v1-blockers
None of the findings are hard v1 blockers: every one requiring attacker-supplied values is **Admin- or
settings-gated** against a plane that admins already control, and the genuinely exposed surfaces (inbound
webhook, MCP permission mapping, mTLS server validation) have working authentication. The cheap, worthwhile
hardening items are **E-01** (validate git branch) and **E-02** (validate SSH user/host) — both close an
argument/option-injection gap that the codebase's own quoting does not cover, and both have a clear idiom
already present elsewhere to copy.
