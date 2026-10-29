# Changelog

All notable changes to Whiskers are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer
(0.x = pre-1.0, minor bumps may contain breaking changes — noted explicitly).

## [Unreleased]

- R1-16: **Whiskers protects its own control plane, and free text no longer slips past protected resources.** A
  built-in list — containers named `whiskers*`, `serverwatch*`, `socket-proxy*`, `docker-socket-proxy*`,
  `ghostunnel*`, `step-ca*`, `nginx-proxy-manager*` and those in `WHISKERS_SELF_CONTAINERS`, plus the data
  volume's directory under `/var/lib/docker/volumes/`, mTLS / step-ca / ghostunnel directories (also every
  managed host's `/opt/dockerproxy`, which holds its tunnel key and CA) and the Docker
  socket (also as a mount of a directory above them, such as `/` or `/var/run`) — is protected on every surface for
  every change, whatever the guardrail policy lists. Touching it (or a resource the policy protects) is refused for
  API keys, agents instructed over MCP, automations and Operators; an Admin in the web UI or the web chat gets an
  approval at Admin level instead, single-use and audited (with *Require a second approver* on, another Admin must
  give it). Protected names are now also found in commands, SQL and compose files (word by word, with path
  segments; a command is read as the shell reads it, so quoting tricks such as `whis""kers` do not hide a name), by
  a container id or id prefix written in a command, by a container's other names, among the stopped containers a
  cleanup removes, and in what a deployment mounts — `deploy_app` volumes and every host path and volume of a compose
  deployment, also a local volume that binds a host path through `driver_opts: device:` and the volumes a service
  takes over with `volumes_from` (another service's, or a container's — looked up on the server by id, name or id
  prefix; one that is not found refuses the deployment); a raw compose file is read as YAML for its mounts and names
  (flow style, anchors, escapes decoded), and one that is no YAML is refused (the Docker socket and the data volume are part of the control plane);
  the control plane's names apply to containers, volumes and networks, not to a server called `whiskers-vps`. Free
  text that is completed at run time counts as touching a protected resource: in a command anything the shell
  expands (`$VAR`, `$(…)`, backticks, `{a,b}`), `eval`, `xargs`, decoding, or inline code of another interpreter
  (`python -c`, `perl -e`, `awk … system(`); in SQL dynamic SQL, `COPY … PROGRAM`, file-reading functions,
  `U&"…"` escapes, inline functions and untrusted languages (`plpython3u`), and client commands the text is fed to
  (psql `\i`, `\gexec`, `\c`, `\!`; mysql `source`; sqlite `.read`; mongosh `load()`). A `.env` saved in the web UI
  decides what the project mounts too, and a SQL import or seed decides the database it runs in. A mount whose source
  comes from an `.env` value is decided but never recorded (the audit keeps the length of `mounts`, the decision
  record `(withheld)`). A policy pattern that equals a control-plane pattern makes a hit approvable only where the
  control plane's own check finds it too (not a server named `whiskers-vps`); text that cannot be analysed is
  refused first, whatever names stand beside it. A withheld mount is never quoted in a refusal either. The
  SQL console, SQL import and seed and the raw compose deploy now hand their payload to the policy (Artisan
  migrations carry no free text: their command is fixed); the audit keeps only its length, and an approval raised from the web UI shows only the length of a
  command, SQL or compose file (refusal reasons never quote words of it); a card that shows the text (an agent's
  chat or MCP call) masks SQL password literals (`PASSWORD '…'`, `E'…'`, `IDENTIFIED BY …` / `"…"`, also with comments
  between the tokens) as it masks other
  secrets, value by value, and so do the audit log and the host-command logs. **Upgrade notes:** a scheduled cleanup
  task no longer removes unused networks (which ones are unused cannot be decided before the prune): it removes
  stopped containers, dangling images and build cache; calls that used to
  pass are now refused or need an Admin approval — an MCP key or a scheduled task restarting the Whiskers container
  or its proxy; a command or query that merely mentions a protected name (also inside a domain such as
  `whiskers.example.com`); any command from MCP or automation that uses a shell variable or `$(…)` (for example a
  scheduled CustomCommand with `$(date +%F)`), `xargs`, `awk '{print $1}'`, `python -c`, a pipe into a shell or an
  interpreter (`curl … | sh`), a script (`bash deploy.sh`, `./deploy.sh`, `/opt/x/run.sh`, `python3 x.py`), `make` or
  `npm`, any interpreter (`python3 …`, `node …`), `xargs` by any path, `find -exec` from `/`, a changing
  `docker compose` / `docker-compose` / `docker stack` command (`up`, `down`, `rm`, … — the compose file, not the
  command, says what it touches), `docker system|container|volume|network prune` (what it removes is named nowhere;
  `docker image prune` stays allowed), a compose bind mount whose relative source climbs to `/` (`../../..`), or `rm`/`mv`/`chmod` on `/var/lib/docker`; renewing or issuing mTLS certificates and restoring a self-backup
  (an Admin approval in the web UI, refused for keys);
  editing an nginx site that proxies to Whiskers; deploying your own `step-ca` or `ghostunnel`; SQL with dynamic
  statements or inline functions (`EXECUTE`, `PREPARE`, `CREATE FUNCTION`, typical in migrations and `pg_dump`
  imports — a scheduled SQL task is refused, a UI import needs an Admin approval), also `decode(`, `chr(` and
  `convert_from(`; a command or compose file with a 12-digit hex word (an image digest, a git SHA) while the
  server's containers cannot be listed; a compose deployment
  that mounts the Docker socket, `/`, `/var/run` or `/var/lib/docker` (cAdvisor, node-exporter, Portainer) or a volume
  whose source uses a variable the project's `.env` does not set; a scheduled `volume.backup` of the
  `serverwatch-data` volume run by a task (refused — back Whiskers up with its own self-backup instead); AutoUpdate
  and AutoPatch of the Whiskers container itself (refused each cycle with an `automation_refused` notification —
  update Whiskers from the web UI); a raw compose file in the UI deploy that uses a variable without a default
  (Admin approval, since the target directory's `.env` is not read). Rename a
  container that only shares the prefix, put the command into a script file on the host, or run the action from
  the web UI as an Admin. The Admin exception covers the control plane only: a resource protected by the policy's
  own pattern stays refused for everyone, as before, wherever in the call it is named — and while the policy lists
  patterns of its own, free text that cannot be analysed is refused for everyone too (it could name one). The data
  volume is recognised under a compose project's prefix as well (`prod_serverwatch-data`).
- R1-15: **who may decide an approval, until when, and for how long a chat's approval lives.** Approving and
  rejecting follow the same rules: the approver holds the action's effective level (Viewers decide nothing, and the
  chat now passes the approver's current role), and nobody decides their own non-interactive request — direct MCP,
  AI trigger, scheduled task, webhook (PC-D4). The agent chat and UI clicks may still be confirmed by the person who
  made them, recorded as a self-approval on the Approvals page, unless the new guardrail option **Require a second
  approver** is on (off by default; left out of the stored policy while off, so existing policy versions stay the
  same; a legacy single-policy file that sets it keeps it when migrated). An expired approval can no longer be decided (checked by the store and the database update; a decision whose write waited past the expiry is not committed), overdue
  approvals are expired every 30 seconds so a waiting chat is released, and a grant is usable only within its
  lifetime (checked again when its use is committed; on PostgreSQL inside its retrying execution strategy). Clearing or closing a chat, leaving the page or losing the browser connection stops its run and cancels
  its pending approvals, so approving one elsewhere can no longer resume it unattended (calls it left open are recorded as not completed, so the chat can go on); sessions wait on unique operation ids instead of provider tool-call ids. One
  principal has at most 25 approvals waiting, counted for the user behind every chat too. The chat's Approve and
  Reject buttons stay disabled until the approval is recorded, and both decide only through it. New optional setting
  `WHISKERS_PUBLIC_URL` (`Whiskers:PublicUrl`): the address people open Whiskers at; when set, approval
  notifications sent by e-mail, Telegram, ntfy and other channels carry a direct link to the approval. Approval notifications name the approval, risk, target and a link to
  it (the target's kind, and its id and server only where Whiskers took them from its inventory; the bell opens the
  card), never the arguments or a value a caller can put into the call; Viewers no longer see an approval's
  arguments. Additive migrations (SQLite + PostgreSQL)
  add `RequesterIds` and `SelfApproved` to `Approvals`. **Upgrade note:** a pending approval raised before the
  upgrade records no requester and is cancelled at startup — the caller asks again; approved, unused ones keep their
  grant.
- Tests: the full-app boot tests that switch modules off ("lean boot") or set `Auth:Disabled` now really boot that
  configuration. Program reads those settings while the builder is set up, before a test's in-memory configuration
  is layered in, so these tests had been booting every module (and the real authentication setup) while reporting
  green. `BootSettings` passes the settings as environment variables too (all or nothing, restored afterwards);
  every boot test that sets one of these now asserts the complete module set and the authentication setup it ended up with
  (`BootSettings.AssertBoot`, an inherited flag fails it too), lean boots switch default-off modules off explicitly
  (`BootSettings.OnlyCore`), and `BootMatrixTests` compares against module sets written out per case. The only product
  change: `ModuleCatalog.All()` is internal instead of private, for that check.
- R1-14: **approvals are durable and fully bound.** Every approval is written to the new `Approvals` table (additive
  migrations for SQLite and PostgreSQL; the SQLite → PostgreSQL migrator copies it) before it is shown, decided or
  used, so a restart no longer loses a decision or lets an approval run twice, and the history on the Approvals page
  is no longer cut at 200 entries. An approval is now bound to the principal, the credential it authenticated with,
  the capability and its level, the normalized arguments, the **resolved target** and the **policy version** it was
  decided under, and stays usable for ten minutes after the approval: a call that differs in any of them — another
  key, another target, a changed preset — needs a new approval. Using an approval is a single conditional update in
  the database, so of concurrent calls (also across a restart) exactly one runs; an approval that cannot be recorded
  is not raised and the call is refused. **Upgrade notes:** the migration runs on the first start. Approvals that
  were pending in memory before the upgrade are gone (as on any restart before); after the upgrade a restart keeps
  open approve-then-retry approvals and unused grants, while a pending agent-chat approval is cancelled (its chat
  session ended with the restart) — ask the agent again. To go back, start the previous image: it ignores the new
  table.
- R1-13: **AI triggers are read-only unless an Admin grants them writes** (PC-D6, PC-D7). A trigger may change
  something only when an Admin lists both the **capabilities** it may perform and the **targets** (names or ids, `*`
  and `?` as wildcards) it may act on; saving such a grant makes that Admin the trigger's **owner**. The run then
  starts with the owner's current level, and the Mutation Gateway checks **every** mutation made for the run — also
  one nested in it, such as the actions of a scheduled task the trigger runs, and every resource an operation also
  touches, such as the containers a network removal cuts off — against the grant and the owner's current authority:
  anything outside the grant is refused. Targets are matched on what Whiskers resolved (an id, or a name from its
  inventory), never on a name the call supplied, and a touched resource counts as granted by any one of its names
  or ids. Deleting a Hetzner snapshot can no longer be granted to a trigger at all (the grant could not bound which
  snapshot); a saved grant for it is dropped. Saving the trigger page after another admin saved or a trigger was suspended is refused with a hint to reload, so a stale page cannot lift a suspension or widen a grant again. **Every trigger now has an owner** (the Admin who created it, changed its
  grant or switched it on again), re-checked before each run; an owner who lost the right (demoted below a granted
  capability, removed from the whitelist, account deleted) suspends the trigger and notifies the Admins
  (`automation_suspended`); switching it on again makes the Admin who does it the owner. Every Confirm of a trigger
  run — also for mutations nested in it — is refused in the gateway instead of raising an approval, and every audit
  row of the run (nested ones included) carries `trigger:<id>`. The event that starts a run (container, image and
  server names, the log line) is handed to the agent as delimited JSON data with a random marker per run, never as
  part of its task. The AI triggers page shows the grant, the owner and a suspension, and its help text no longer
  claims that confirmations reach you. **Behaviour change / upgrade notes:** every existing trigger is **read-only**
  after the upgrade, whatever `MaxLevel` it had (the field is gone and ignored when read): a trigger that used to
  restart or update containers now only investigates and reports. Existing triggers also have **no owner**, so each
  is suspended (with a notification) the first time it fires; to keep one, open AI triggers, switch it on again
  (or off and on for one that never fired), grant the capabilities and targets it needs and save — you become its
  owner. Operations that also touch other resources need those granted too. Nothing in `ai-triggers.json`
  needs to be edited by hand; to go back, start the previous image (it ignores the new fields, and its triggers
  fall back to `MaxLevel=write`).
- R1-12: every policy decision of the Mutation Gateway is **persisted before anything acts on it** (new tables
  `PolicyDecisions` and `PolicySnapshots`, additive migrations for SQLite and PostgreSQL; the SQLite → PostgreSQL
  migrator copies both). A record holds the principal, surface, capability and required level, the resolved target,
  a normalized input hash (keys sorted at every level), the policy version (content hash of the exact policy
  evaluated), the preset, the matched rules, the verdict and the reason; a Deny is recorded too. If the record cannot
  be written the mutation is refused. Audit rows (intent, outcome, refusal) carry `decisionId`; an approval names the
  decision and preset that asked for it. Policy versions are content-addressed: an edited preset is a new version and
  stored versions never change. **AI trigger runs are pinned** to the trigger's preset for the whole run (the agent
  session and the gateway both decide against it); switching or editing the active preset no longer reaches a running
  trigger, the global kill switch still does, and a fail-closed guardrail store stops it. Mutations nested in a
  trigger's call (a scheduled task it runs, also one a person created) are decided against the trigger's preset with
  the agent rules (tool lists, forbidden arguments, confirmation), and a cleanup task run there protects what that
  preset protects. Decision records never store URL credentials (a Git app's repository URL). **Behaviour change:** a
  trigger whose preset no longer exists does **not run** any more (it used to fall back to the first preset); the
  refusal is audited (`agent.run`, unsuccessful) and the AI triggers page marks the missing preset. Likewise an active
  preset that does not exist yields the fail-closed policy instead of the first preset. An agent session has the
  gateway decide (and record) a call before it pauses for an approval or reports a block, and follows exactly that
  recorded verdict, so the approval names the decision that asked for it. A decision that cannot be recorded is
  refused with a localized message. **Upgrade notes:** check that every AI trigger names an existing preset. The migration only adds the two
  tables and runs at start; if it fails, Whiskers does not start and nothing else has changed — restore `/app/data`
  from the backup taken before the upgrade (or a Whiskers self-backup) or start the previous image again, which
  ignores the new tables. Decision rows are not pruned yet.

- R1-11: every security configuration write goes through the Mutation Gateway as an **Admin-only** capability:
  the Settings sections (`config.save`), whitelist (`config.whitelist`), roles (`config.roles`), MCP tool and key
  permissions (`config.mcp_permissions`), the agent's LLM provider, key and system prompt (`config.agent`), MCP key create/delete (`mcp.key_create`, `mcp.key_delete`), guardrails and
  presets (`guardrails.save`, from the Guardrails and AI operations pages), vault secrets (`vault.secret_set`,
  `vault.secret_delete`, also a Kubernetes server's kubeconfig) and registries (`registry.save`, `registry.delete`).
  Each is authorized at the moment of the click (a Viewer or a demoted user is refused whatever the page shows),
  decided and audited before the store is written; secret values and keys are never arguments. While the kill switch
  is on it refuses all of them; the one exception is the new **Switch kill switch off** button on the Guardrails page
  (`guardrails.kill_switch_off`: Admin, a person in the UI only, audited, changes nothing but `ReadOnlyMode` of the
  active preset). A fail-closed guardrail store (unreadable `guardrails.json`) refuses every save too, also of the
  guardrails — **upgrade note:** to recover, fix or remove `guardrails.json` on the host and restart Whiskers
  (removing it recreates the SafeDefault preset). Agent provider saves are serialized as well. MCP
  permissions, the vault and registries now publish a change only after it is written, a Settings save of several
  sections (notifications, channels) is one file replacement, and a registry credential is stored under a new vault
  key per change that only takes effect with the registry entry naming it (a pull during the change reads the new one);
  a changed kubeconfig goes to a new vault key that only takes effect with the saved server record;
  removing a Kubernetes server writes the server list first and only then removes its kubeconfig, within the one
  `server.remove` decision (`ServerRemoval`) — so a failed write leaves the live configuration as it was (`UiMutationGatewayCoverageTests`). The first-run setup wizard writes the whitelist outside
  the gateway, before any admin exists (documented baseline entry).

- R1-09: webhooks have an **owner** — whoever creates the webhook, or an admin taking it over on the Webhooks page
  (new Owner column). An external call to `/api/webhooks/{id}` now runs its action through the Mutation Gateway on
  the owner's authority, re-checked at each delivery — every delivery is decided and audited, also one without an
  owner or whose target cannot be resolved — for all four actions: restart and rebuild as
  `container.restart` / `container.update` on the resolved container (a stopped one too; an ambiguous id prefix is
  refused), compose deploy as `compose.deploy` plus a decision per container of its plan, Git deploy as
  `gitdeploy.deploy` by the webhook's owner and then by the app's owner (R1-08). A webhook without an owner, or whose
  owner lost the authority (role, whitelist, account, key), is refused, switched off and an admin is notified; any
  other refusal (kill switch, protected resource, unknown target, the Git app owner's, unwritable audit) is reported
  to an admin and leaves it on. A webhook whose server was removed can still be switched off and deleted. A compose planning error is not passed into the audit, notifications or the webhook log (it can quote `.env` values); the Test button shows it. A compose project name interpolated from the `.env` or `name:` is recorded in decisions and the audit as its source expression (e.g. `${DB_PASSWORD}`), a literal name as it is (`ComposePlan.ProjectNameForAudit`). Creating,
  taking over, rotating the secret, switching on/off and deleting a webhook are decided by the gateway too
  (`webhook.save`, `webhook.rotate_secret`, `webhook.set_enabled`, `webhook.delete`, all Admin), on the stored
  webhook; the secret is never a decision argument. The Webhooks page's Test plans the action as the trigger does
  (`WebhookTargets`), decides it as the clicking person, and the signed call then runs on the owner's authority. Git
  deployments and webhooks fired by their owner now get the rules for a person (PC-D1), not an agent's
  (`WebhookMutationTests`).

- R1-08: Git deployments have an **owner** — whoever saves the app, or an admin taking it over on the Git deploy
  page. A webhook deploy runs on the owner's authority, re-checked every time through the automation runner: the
  deployment and every container of its compose plan are decided as the owner; an owner who lost the authority (role,
  whitelist, account) is refused before anything is fetched. The decision is made on the **stored** app (the target
  resolver looks up its name, server and revision; another server or an outdated revision is refused), and the
  deployment runs exactly that snapshot: the app must still be at the same revision with the same inputs before the
  fetch and again before the build, otherwise it is refused. A save from a stale copy is refused too. The Webhooks
  page's Test of a Git deploy decides as the clicking person and then runs on the owner's authority
  (`GitDeployMutationTests`).

- R1-07: every compose deployment is planned before anything is written (`ComposePlan`, `IComposePlanner`): the
  project `docker compose` really uses (`-p`, else `COMPOSE_PROJECT_NAME` from the `.env`, else the top-level
  `name:` with `.env` interpolation, else the directory name, normalised as compose does), the compose files (a
  directory's `COMPOSE_FILE` or default file plus override, or the container's recorded `config_files` for a `.env`
  change), and every existing container the deployment recreates, removes as an orphan (`--remove-orphans` or
  `COMPOSE_REMOVE_ORPHANS` in the `.env`) or collides with by `container_name` / default name. Each of those containers is decided — the compose editor, App Store, Deploy page,
  container `.env` save, Webhooks **Test** and Git deploy once per container, MCP / agent `deploy_compose` and
  `set_container_env` as related resources of the one decision — before the first write. Execution then runs the
  plan with an explicit `-p`, `--project-directory`, `-f` (and `--env-file` when there is one); right before writing
  it plans again and refuses if that plan touches a container that was not decided. What cannot be planned (a
  variable without default in a name or a compose switch that is not in the `.env`, a multi-line `.env` value,
  `include`, `extends` from another file, invalid YAML, an unreadable file) refuses the deployment
  (`ProtectedResourceResolutionTests`). A `${VAR:-default}` whose variable is not in the `.env` is planned with the
  default; compose gives a value from the host's environment precedence over the `.env`, which the plan does not
  see. The host environment's `COMPOSE_REMOVE_ORPHANS` / `COMPOSE_IGNORE_ORPHANS` are cleared for the command.
  A compose file with several YAML documents is refused. A Git deploy fired by the webhook **Test** button checks
  its plan against the Test decision, which covers no containers yet: until R1-08 it is refused when the deployment
  touches an existing container.

- R1-06: every mutation's target is resolved against the inventory before policy — servers (an empty server id is
  the configured default server, decided by the gateway), containers, networks with the containers attached to them,
  cloud servers (Whiskers id or name), scheduled tasks (id or unique name), DNS zones (normalised, within
  `Dns:AllowedZones`) and Kubernetes workloads (their **live** controller). An unknown or ambiguous target is refused
  before policy. MCP and agent tools then run on the resolved identity (canonical id and server id), not on a second
  lookup of what the caller typed. A protected container attached to a network that is removed, or connected to /
  disconnected from one, and a workload's current controller are matched against protected patterns
  (`TargetResolutionTests`).
- R1-06: AutoUpdate no longer turns the server id `local` into "the default server": a container listed on `local` is
  decided and recreated on `local`, also when another server is the default. Webhooks decide (Test button) and run
  on the same server and container (`WebhookTargets`).
- R1-06: a tool that takes a `serverId` always receives the server the gateway decided on, also when the caller left
  it out (a missing server is the default server to the gateway, but some tools read it as `local`). AutoPatch, the
  Docker workload provider and the Networks, Scheduled tasks, Volume backups, App Store and Webhooks pages no longer
  turn `local` into "the default server" either. Refused targets carry a reason code (unknown, ambiguous, not
  resolvable) and are shown in the user's language.

- R1-05 review: the two refusals the effective requirement adds (underlying capability switched off, requirement
  not determinable) are shown in the user's language on the pages; covered end to end through the real gateway,
  approval gate and store (`SchedulerApprovalEndToEndTests`).
- R1-05 review: interactive approvals (agent sessions) are raised at the operation's effective requirement, and the
  approval gate refuses to reuse an approval raised below it, so an Operator can no longer approve a scheduled custom
  command, retention or self-backup. The scheduler requirement resolver reads `taskType` in every form the tool
  accepts (name or number) and fails closed to Admin otherwise, reads `maxBackups` like the tool (`"0"` is no
  retention), and applies stricter or disabled settings of the underlying capability from Settings → MCP.
- `scheduler.delete` is Write on every surface: the Scheduled tasks page now takes the level from the capability
  catalog (it required Admin). Deleting a task is not root-equivalent; creating, updating and running stay gated by
  the task's effective requirement.

v1.0 readiness, work package 1 (immediate fixes from the Phase 0 audit, `docs/v1/TECHNICAL_AUDIT.md`).

### ⚠️ Upgrade notes

- **R1-11: while the kill switch is on, configuration can no longer be saved** — Settings, roles, whitelist, keys,
  vault, registries and guardrails included. Switch it off with the button on the Guardrails page first. Registry
  changes now need Admin (they had no role check). An unreadable `guardrails.json` can no longer be fixed by saving
  on the Guardrails page: repair or remove it on the host and restart Whiskers (without the file the safe default is
  created).

- **R1-09: webhooks created before this release have no owner and stop working.** An external call (and the Test
  button) is refused and switches the webhook off (an admin is notified) until an admin takes it over on the Webhooks
  page (person icon in the Owner column) and switches it on again; CI callers keep their URL and secret. A webhook whose owner later loses Admin (restart and rebuild need Operator),
  is removed from the whitelist or loses their account is switched off on its next call, and an admin is notified.
  Managing webhooks still needs Admin, now also checked by the gateway (kill switch, audit).

- **R1-08: Git deploy apps saved before this release have no owner.** A webhook (or the Webhooks page's Test) no
  longer deploys them until an admin takes them over on the Git deploy page (person icon in the Owner column);
  deploying from the Git deploy page itself still works. Saving an app makes you its owner. A webhook deploy now fails
  when the owner lost Admin, was removed from the whitelist or lost their account.

- **R1-07: compose deployments name their project and files explicitly.** Whiskers now runs
  `docker compose -p <project> --project-directory <dir> -f <file>…` with the project and files it planned, instead of
  `cd <dir> && docker compose up`. The project is the one compose itself would have picked, so existing projects keep
  their name. Differences: a deploy from the compose editor, App Store or MCP `deploy_compose` uses exactly the file
  it writes — a `compose.yaml` or `docker-compose.override.yml` left in `/opt/deployments/<project>` is no longer
  picked up; a webhook deploy directory must contain a compose file itself (compose no longer searches parent
  directories); a `.env` change keeps the container's project even if the new `.env` sets another
  `COMPOSE_PROJECT_NAME`. A compose file whose `name:` or `container_name` uses a variable without default that is
  not in the `.env` (only in the host's environment), a `.env` whose compose switches do the same or that holds a
  multi-line value, and a file using `include` / `extends` from another file can no longer be deployed through
  Whiskers. `COMPOSE_FILE` in a webhook directory's `.env` is honoured as before; in `/opt/deployments/<project>`
  (editor, App Store, MCP) the written file is used instead.
- **R1-06: unknown or ambiguous targets are refused.** A call naming a server, network, cloud server or task that
  does not exist, or a name that fits more than one (two servers called "web", a network id prefix shared by two
  networks, two tasks with the same name), is refused before anything runs; use the id. A server id that differs
  from the stored one only in case is resolved to the stored id.
- **R1-06: an MCP or agent call without `serverId` acts on the default server, for every tool that takes a
  server.** Some tools
  (`set_container_env`, database backups, …) used to read a missing server as `local` while the gateway decided on
  the default server; they now receive the default server's id. Pass `serverId` where you mean `local`.
- **R1-06: a scheduled task saved without a server acts on the server with the id `local`**, for its gates as well
  as for its execution (the execution always did; the decision used the default server). Set the server explicitly
  where `local` is not the intended machine.
- **R1-06: a webhook saved without a server acts on the server with the id `local`.** Restart and rebuild webhooks
  used the default server before, compose-deploy webhooks used `local`; now all four actions use `local`. If your
  `local` server is not the machine such a webhook should act on (or you have no server with that id), set the
  webhook's server explicitly. A webhook whose container id prefix matches more than one container no longer acts
  on the first match; it fails with "Ambiguous".
- **R1-05: one capability level table; several MCP tools now require Admin.** The capability catalog
  (`CapabilityCatalog`) is now the single source of the level of every mutating capability; MCP's
  `DefaultToolLevels` takes its mutating entries from it. Where the UI and MCP disagreed, the stricter level
  won: no MCP level was lowered. One UI level was: creating and deleting a scheduled task on the
  Scheduled tasks page now needs Operator (was Admin), and the gateway then raises it to Admin per task type
  (custom command, self-backup, DB backup, retention) — Operators can now manage restart, cleanup and volume
  backup tasks without retention there. API keys and agents at Write lose these tools:
  - Firewall `add_firewall_rule`, `remove_firewall_rule`; nginx `update_nginx_config`; systemd
    `manage_systemd_service` — Write → Admin (their UI pages already required Admin).
  - `set_container_env` — Write → Admin: it writes the compose `.env` (any key) and runs `docker compose up -d`,
    the same root-equivalent operation as saving the `.env` on the container page (already Admin).
  - Database dump `backup_database` and certificate renewal `renew_ssl_certificate` — Write → Admin (their UI
    pages already required Admin). A scheduled **DB backup task** now needs an Admin owner, with or without
    retention.
  - Scheduled tasks: creating and deleting a task follow the catalog on the page as well (Operator); the gateway
    then holds each task type to what it runs (a custom command, retention, a DB dump or a self-backup needs Admin).
  - Networks `create_network`, `remove_network`, `connect_container_to_network`,
    `disconnect_container_from_network` — Write → Admin (Networks page requires Admin).
  - Cloud `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot` and
    Hetzner `hetzner_disable_rescue`, `hetzner_enable_backups`, `hetzner_disable_backups`,
    `hetzner_change_server_type`, `hetzner_delete_snapshot` — Write → Admin (Cloud page requires Admin;
    `hetzner_enable_rescue` was already Admin).
  - `gitdeploy.deploy` (UI) — Write → Admin: it is root-equivalent. The Git deploy page now shows the deploy button
    to Admins only (it was shown to Operators).
  - `create_scheduled_task` / `run_scheduled_task` stay Write, but the requirement — and therefore the approver's
    level — is now resolved per task before the caller check and the approval: CustomCommand (`host.exec`),
    SelfBackup (`selfbackup.create`/`selfbackup.prune`) and DB/volume backups with retention (`maxBackups` > 0,
    the default 7; `db.backup_delete`/`volume.backup_delete`) require Admin, and so does every DB backup (a dump
    holds every row). Pass `maxBackups=0` to keep a **volume** backup task at Write. The Scheduled tasks page sends
    the same arguments and asks the same resolver for "Run now". Retention is read with the executor's own rule (`TaskExecutor.RetentionOf`), the one the
    Scheduled tasks page already uses for "Run now".
- **Deploying from the UI now requires Admin** (was Operator): the deploy form, the compose editor and the app store.
  A compose file can mount `/` or run privileged, which is root on the host. The MCP deploy tools already
  required Admin.
- **The first-run wizard asks for a one-time setup token.** It is printed by the installer and written to
  `/app/data/setup-token.txt` (mode 0600), or preset with `WHISKERS_SETUP_TOKEN`. Instances that already have
  an admin are not affected.
- **`AUTH_DISABLED=true` now only serves the trusted network.** Allowed are loopback, RFC1918, CGNAT/Tailscale
  and IPv6 ULA clients, with an IP or `localhost` as host name and no cross-site `Origin`. Behind a reverse
  proxy with a host name, add it via `Auth__DisabledAllowedHosts__0=…`.
- **MCP and agent mutations run through the Mutation Gateway** (ADR-0005 step 2.1). What changes for callers:
  - A tool switched off in Settings → MCP, or raised to a stricter level there, now applies to web users, the
    agent and AI triggers too, not only to API keys.
  - A key's `AllowedTools` list can no longer lift it above its level: a Read key listing `start_container` is
    refused. Give such a key the level the tool needs.
  - An unknown or ambiguous container is refused before the guardrails run, with the same "Container not found" /
    "Ambiguous container id" text as before.
  - Mutating tools have deadlines (2 to 30 minutes by capability; `execute_command` 11 minutes, above its own
    600-second limit). A tool that runs longer is reported as failed.
  - The agent runs a mutation that needed a human only with the approval registered for exactly that call. A
    confirmation answered in the chat without a registered approval no longer executes; the agent is told to
    repeat the call, which raises a regular approval.
  - Each MCP or agent mutation now leaves **two** AuditLog rows: the gateway's row (decision, approval, intent,
    outcome; `Details` is JSON with `operationId`) and the tool's own result row as before.
  - A mutating tool that answers with an error text (container not found, command exit code ≠ 0, failed backup …)
    now returns that text with `isError: true` over MCP, and the agent sees it as a failed call. The gateway row
    records it as `Failed`.
- **UI actions run through the Mutation Gateway** (ADR-0005 step 2.2, PC-D1). What changes for people using the web UI:
  - The **read-only kill switch** and **protected resources** now apply to UI actions too. With the default preset
    the Whiskers container itself (`whiskers*`) can no longer be stopped, restarted, updated or removed from the
    Dashboard or the container page; remove the pattern from the active guardrail preset if you need that.
  - A tool **switched off** in Settings → MCP, or raised to a stricter level there, also switches off or raises the
    matching UI action (e.g. `restart_container` → the restart buttons).
  - A refused action shows the reason as a warning; the gateway's AuditLog row records it.
  - Also covered: Kubernetes start/stop/restart on the Dashboard (decided on the Deployment/StatefulSet the pod
    belongs to), saving a container's `.env` (every service of its compose project is decided, since
    `compose up` may recreate them all), Artisan migrate/rollback, SQL file import, deploys from the compose editor
    and the App Store (every existing container of the project is decided as well: a protected `payments-db` in
    project `payments` refuses the deploy), the **Test** button of a webhook (decided as its real action — restart,
    rebuild, compose deploy, Git deploy — on its real target), adding, editing and removing a server, saving AI
    triggers, server onboarding, and creating, deleting and restoring a Whiskers self-backup.
  - A **cleanup task** (`docker system prune`) is refused while a stopped container matching a protected pattern
    exists on that server — the prune would remove it. A task on `local` checks the local server, not the default
    server.
  - "Run now" of a scheduled task is checked for the task's own action and target as well (a restart task on a
    protected container is refused); over MCP, `run_scheduled_task` also needs the caller's access to
    `restart_container` / `backup_database` / `execute_command` for those task types.
  - A UI action whose service answers with a failure instead of an exception (failed command, query error,
    `false`) is recorded as `Failed` in the gateway row, not `Executed`.
  - The gateway row no longer stores free-text arguments (SQL text, commands, file content); only their length.
  - No approval is asked for a person's own click; tool allow/deny lists and argument patterns stay agent rules.
- **Scheduled tasks, AutoUpdate and AutoPatch run on their owner's authority** (ADR-0005 step 2.3, PC-D6). What
  changes:
  - A new task records who created it (the signed-in user, or `key:<id>` for an MCP key; for the agent, the user or
    key it acts for). Creating a task without such an owner (an AI trigger, a session without an email) is refused. Every run re-checks that
    owner's current role or key level and refuses the run when it no longer covers the task (owner demoted, removed
    from the whitelist, key deleted or disabled). A refused run is recorded as failed, sends an
    `automation_refused` notification, and leaves a gateway AuditLog row.
  - **Tasks from before this version have no owner, and owners are required** (`Automation:RequireOwner`, default
    `true` since R1-04): an ownerless task is refused and switched off until an Admin takes it over — "Take over
    all" or per row in Scheduled Tasks, which shows them with a warning. To keep them running for a while instead
    (grace period, audited as "no owner" and limited to the capability they run), set
    `Automation__RequireOwner=false` explicitly.
  - **AutoPatch does not run until an Admin takes it over** in Settings → Automation owners. `AutoPatch:Enabled`
    alone is no longer enough; the nightly cycle logs a warning and skips. AutoUpdate without an owner is refused
    as well (unless the grace period is switched on as above); take it over there too.
  - What a task needs from its owner: a **custom-command** task needs an Admin owner (it runs as root on the host);
    a backup task whose **retention** deletes old backups (volume and DB backups) needs an Admin owner for the
    deletion; a **self-backup** task needs an Admin owner; the **cleanup** task (`docker system prune`) needs
    Operator. Restart tasks and volume backup tasks without retention need Operator; **every DB backup task needs
    an Admin owner** (R1-05: a dump holds every row) — take an existing DB backup task owned by an Operator over as
    an Admin, or it is refused.
  - **A task owned by an API key keeps every rule that binds the key**: tool deny/allow lists, forbidden arguments
    and confirmation. Where the policy would ask for a confirmation (the default preset does for every write by a
    key), the run is refused — nobody is there to confirm it. Have a person take the task over, or adjust the
    preset for that key. The refusal is final: it raises no approval in the Approvals page, so approving something
    there can no longer let the next scheduled occurrence through.
  - **A custom-command task is checked against the forbidden-argument patterns of today**, on every run, not only
    when it was created (key-owned tasks). A command that a pattern added later matches is refused.
  - **A task whose owner lost its authority** (removed or demoted user, deleted or disabled key) is flagged in
    Scheduled Tasks; an Admin can take over any task there, alone or with "take over all".
  - **A refusal reason never quotes a command or SQL text**: the gateway removes payload values before the reason
    reaches a log line, a notification, the task history or the UI.
  - **The AutoPatch preview over MCP** (`preview_auto_patch`) runs on the caller's authority and decides every
    container like the nightly cycle; a key whose preset confirms writes gets every container refused.
  - **AutoPatch decides every container on its own** (`container.update` of that container), in a dry run too: a
    protected container, or one the owner may no longer update, is skipped as `Aborted (refused: …)` instead of
    patched or tried. A patch that ends as `Unknown`, `RolledBack` or `RollbackNotVerified` is audited as failed,
    and so is the night it belongs to.
  - **An unreadable or invalid `automation-owners.json`** (also a directory at that path, or a file that cannot be
    opened) blocks AutoPatch and AutoUpdate (logged as an error; AutoUpdate records a failed update) until an Admin
    takes **each** of them over again; it no longer counts as "no owner". Taking one over keeps the other blocked,
    across restarts; the unreadable file is kept as `automation-owners.json.unreadable-<timestamp>`.
  - The kill switch and protected resources apply to scheduled runs, AutoUpdate and AutoPatch. With the default
    preset a scheduled backup of a `whiskers*` / `serverwatch*` volume or a restart of the Whiskers container is
    refused; remove the pattern from the active preset if you rely on it.
- **Arbitrary SQL needs Admin everywhere** (PC-D3): the database query in the container page and the
  `execute_query` MCP tool were Operator / write.
- **Container shells need Admin** (PC-D2): `/terminal/{server}/{container}` and the terminal tab in the container
  page were Operator.
- **"Run now" needs Admin for a custom-command task, a self-backup task and any backup task with retention**
  (`maxBackups`): retention deletes old backups, which is decided as `volume.backup_delete` / `db.backup_delete` /
  `selfbackup.delete`. Restart, cleanup and backup tasks without retention stay Operator.
- **Unattended items need an owner and are suspended when it loses its authority** (R1-04, PC-D6, INV-04):
  - `Automation:RequireOwner` now defaults to `true`: a scheduled task or AutoUpdate without an owner is refused.
    Take such items over in Scheduled Tasks, or keep the grace period with `Automation__RequireOwner=false`.
  - A run refused because its owner lost their authority — local account deleted, removed from the whitelist,
    demoted below what the item does (including retention deletes of a backup task), API key deleted or disabled —
    now **suspends** the item: a scheduled task is switched off — take it over in Scheduled Tasks **and switch it
    on again** (the takeover does not switch it on by itself, since a task may have been off on purpose) —,
    AutoPatch / AutoUpdate are blocked until an Admin takes them over in Settings → Automation owners (AutoPatch
    also stops mid-cycle). If switching a task off fails, every run is still refused and an Admin is notified. Admins get an
    `automation_suspended` notification that no container mute hides. Policy refusals (kill switch, protected
    resources) still only report `automation_refused`.
  - With Google/OIDC sign-in next to local accounts, Whiskers now records which emails have a local account and
    which were seen signed in through Google/OIDC (`known-local-accounts.json` in the data directory). An owner
    whose local account is deleted loses their authority. **After the upgrade, items owned by a Google/OIDC user
    are refused (not switched off) until that user has opened Whiskers once**, or an Admin takes them over — an
    owner without a local account is only trusted once seen signing in federated (fail closed; the same applies if
    the record is lost).
  - Authority loss is judged by what the run actually needs: a level raised in Settings → MCP, and a key's tool
    list, count as well.
  - **Tasks from before provenance was recorded** (created before this release, owner set, no `CreatedVia`) may
    have come from an agent, so they now run with the agent's guardrail rules; with the default preset their writes
    are refused (`automation_refused`). Take each such task over in Scheduled Tasks to run it with the PC-D1 rules
    again — also a task you own yourself (the take-over button is shown for it, and "Take over all" includes it).
  - A task an agent created keeps the agent's guardrail rules when it runs (autonomy level and confirmation, tool
    lists, forbidden arguments). With the default preset such a task's writes are refused; an Admin who takes it
    over makes it a task of their own.
  - Taking an item over records the previous and the new owner's authority in the audit row.
- **Principals have stable ids** (R1-03, INV-01/06):
  - Owners of scheduled tasks, AutoPatch and AutoUpdate are now recorded as `user:<email>` or `key:<key id>`. The
    database migration `PrincipalIdsForTaskOwners` (which also adds `ScheduledTasks.CreatedVia`: who created the task,
    e.g. `agent:<session>`) and the owner file (on load) rewrite plain ASCII emails to `user:<email>`;
    any other value is left as it is and its runs are refused until an Admin takes the item over.
  - Each AI trigger acts as its own principal `trigger:<id>` instead of the shared pseudo key `ai-trigger`; its audit
    rows now name `trigger:<id>` as actor. Renaming a key or a trigger keeps its identity.
  - The MCP call log (Agent History) records callers as `key:<key id>` / `user:<email>` / `anonymous` (was the key
    name); audit rows of API-key calls name `key:<key id>`. The gateway audit `Details.principal` adds the session and
    the kind of the principal acted for.
- **The guardrail policy fails closed** (R1-02, INV-25):
  - A forbidden-argument pattern that is not a valid regex now **refuses every call** (it was ignored). Fix or
    remove the pattern in Guardrails; saving a configuration with an invalid pattern is refused.
  - A pattern or protected-resource glob that takes longer than 100 ms counts as a match and refuses the call.
  - An unreadable or invalid `guardrails.json` (broken JSON, no presets, duplicate names, a missing field or list
    entry, an active preset that does not exist) no longer falls back to another preset or to the default: every mutation is refused, agents and MCP clients get no
    tool, and the Guardrails page shows a single "Fail-closed" preset until an Admin saves a valid configuration.
    The file is not overwritten and a copy is kept as `guardrails.json.unreadable-<timestamp>`. An old
    single-policy file (from before presets) is still migrated into a "Standard" preset; fields it lacks get the
    restrictive default.
- **Guardrails and approvals no longer switch off with the Agent module.** With `Features:agent:Enabled=false`,
  direct MCP calls are still checked against the active guardrail preset, a `Confirm` raises an approval, and the
  Guardrails and Approvals pages stay in the menu.

### Security
- **Agent-created tasks ran with a person's authority, and a deleted local account kept its tasks running**
  (known items 6/7, PC-D6). See the upgrade note.
- **Identities were rebuilt from display names** (TA-31, C-12, INV-01/06): every AI trigger shared one identity, and
  renaming an API key split it into two in the logs. Principals now carry typed ids end to end.
- **Guardrail policy errors allowed the call, and the live policy could be changed without saving** (TA-32/33,
  C-09/15, INV-25/28). See the upgrade note. The store now hands out copies only, and a save becomes live only
  after it was written: a failed write leaves the previous policy active.
- **Unattended runs had no owner and were never re-checked** (TA-04, INV-04, B-10). Scheduled tasks, AutoUpdate and
  AutoPatch now run through the Mutation Gateway as an automation principal acting on behalf of their owner, whose
  authority is read again on every run. A task created by a key that was later deleted no longer keeps running
  with root on the host.
- **UI actions bypassed the kill switch and protected resources** (TA-26, INV-20/21, PC-D1). Every UI call to a
  mutating capability now goes through the Mutation Gateway.
- **Root-equivalent actions at Operator** (INV-19): container shells, arbitrary SQL and running custom commands now
  need Admin (TA-43, B-10).
- **The tool switch and stricter tool levels applied to API keys only** (TA-10, INV-15). They now apply to every
  caller.
- **`AllowedTools` could widen a key beyond its level** (TA-34, INV-16). The level is now checked first.
- **Protected resources are also matched against the resolved container** for MCP and agent mutations: a unique
  id prefix of a protected container no longer slips past the rule (part of TA-09; commands and SQL text are not
  covered yet).
- **Disabling the Agent module removed the guardrails from direct MCP calls** (TA-27, INV-26). The guardrail
  engine, its store, the approval store and the direct-MCP guard are core now; the MCP filter refuses every call if
  they are missing.
- **Critical — Operator could gain host root through the UI deploy** (TA-01). UI deploys need Admin now. App-store
  template values may no longer contain line breaks or control characters, which could inject YAML.
- **First-run setup could be claimed by whoever reached the port first** (TA-02). It now requires a one-time
  token. Removing or demoting the last Admin is refused at the service boundary, so setup can no longer be
  re-opened that way.
- **Local login had no brute-force protection** (TA-03). Five wrong passwords lock the account for 15 minutes,
  login POSTs are rate-limited to 10 per minute per client, and unknown accounts cost the same hashing time as
  real ones.
- **`AUTH_DISABLED` made every network client an Admin** (TA-11). The trusted-network, Host and Origin checks above
  apply. With auth enabled, a cookie carrying a synthetic identity (`AuthDisabled`, `AgentSynthetic`) is now
  rejected; before, anyone holding the DataProtection keys could forge an Admin cookie that skipped the whitelist.
- **The Trivy image is pinned by digest in the code default too** (TA-14). An operator-configured image without
  a digest now logs a warning.

### Added
- **Mutation Gateway, step 2.3 (ADR-0005).** `IAutomationMutationRunner` runs scheduled tasks, AutoUpdate and
  AutoPatch through the gateway on their owner's authority. Owners are shown and taken over in Scheduled Tasks and
  in Settings → Automation owners (`automation-owners.json` in the data directory). The bypass baseline shrinks from
  29 to 23 entries.
- **Mutation Gateway, step 2.2 (ADR-0005).** `IUiMutationRunner` runs a page's action through the gateway as the
  signed-in user. Sixteen pages use it; the bypass baseline shrinks from 75 to 29 entries.
- **Mutation Gateway, step 2.1 (ADR-0005).** Every MCP tool and agent tool call that maps to a capability runs
  through the gateway: tool switch and level, container target resolution, guardrail policy, approval, audit
  intent, deadline, audit outcome. A shared approval gate binds an approval to the operation and the arguments
  hash and uses it once — only after the audit intent is written, so a refused attempt does not spend it, and
  concurrent identical calls share one approval. The agent session pauses on the same (stricter stored) level and
  tool switch as the gateway. The bypass baseline shrinks from 104 to 75 entries.
- **Mutation Gateway, step 2.0 (ADR-0005).** `IMutationGateway` with a capability catalog of every mutating action, a decide → audit intent → execute → verify → audit outcome pipeline that refuses on any doubt (unknown capability, missing level, policy error, audit not writable), and an architecture test that lists every call to a mutating service outside the gateway in `docs/v1/mutation-bypass-baseline.txt` (104 today; the list may only shrink). No entry point uses the gateway yet, so permissions and behaviour are unchanged.
- **Scope guard in report mode.** Mutating service interfaces are wrapped at startup; a mutation that runs outside the gateway still runs, and the first one per capability is logged at Information (`ran outside the Mutation Gateway (report mode)`). Expect these lines until the surfaces are migrated.
- `server.json` for the official MCP Registry (`io.github.LupusMalusDeviant/whiskers`, OCI image on ghcr.io,
  Streamable HTTP at `/mcp` with a Bearer key). The image carries the matching
  `io.modelcontextprotocol.server.name` label, which the registry checks before it accepts `mcp-publisher publish`.
  The 0.15.0 image was rebuilt with the label from `release/0.15` (no application change), so 0.15.0 can
  already be published. A test keeps the version, image tag and label in step with the app version.

### Fixed
- A refused container update on the Dashboard no longer overwrites the saved rollback point; the snapshot is taken
  only once the update has been allowed.
- The installer no longer recommends `Auth__Disabled=true` for first sign-in. It prints the setup token and
  points to `VAULT_KEY`.
- The README renders completely on catalogues that drop raw HTML and relative paths (LobeHub): the header is
  Markdown, images and links are absolute, and the quick start shows the MCP client config for a self-hosted
  instance (`http://localhost:5100/mcp` with a Bearer key).

## [0.15.0] — 2026-10-26

Hardens the MCP surface and finishes the ServerWatch → Whiskers clean-up. MCP keys are now stored only as hashes.
Direct MCP tool calls now pass the same guardrails as the agent. The server ships prompts, resources and full
`serverInfo`.

### ⚠️ Upgrade notes
- **Copy new MCP keys when you create them.** Only the hash is stored now, so *Settings → MCP* can no longer
  reveal or copy an existing key. Existing keys are converted on the first start and keep working.
- **External MCP write calls may now need an approval.** Under the default guardrail preset, write tools
  called directly by an external client (including the Claude Code runtime) raise an approval on the
  *Approvals* page. Repeat the identical call within 10 minutes after approving it. Adjust the active preset
  if that is too strict for your setup.
- **Fresh installs no longer create a default MCP key.** Create one under *Settings → MCP*.
- **Leftover `api-keys.json` / `initial-mcp-key.txt` are deleted on start.** Their keys were no longer
  accepted for any tool call.

### Security
- **The ServerWatch-era flat MCP key store is gone.** `api-keys.json` held unscoped plaintext keys and was
  still accepted as a fallback at the `/mcp` gate. That fallback, the store and the one-time
  `initial-mcp-key.txt` are removed; leftover files are deleted on start. MCP access now works only with a
  key created under *Settings → MCP* with a Read / Write / Admin scope. The setup wizard no longer shows an
  initial key and points there instead.
- **MCP API keys are stored only as SHA-256 hashes.** New keys come from the CSPRNG (256 bits) and are shown
  once at creation; `mcp-permissions.json` keeps only the hash and the last four characters. Existing
  plaintext keys are converted on the first start and keep working. A fresh install no longer creates an
  unseen default key, a settings save can no longer alter key material, and rejected tokens are no longer
  logged with a prefix. **Behaviour change:** the key column in *Settings → MCP* can no longer reveal or
  copy an existing key — copy a new key right after creating it.
- **Guardrails now apply to direct MCP tool calls.** An external client calling a tool directly was bounded
  by its key's scope only; protected resources, forbidden arguments, the read-only switch and required
  approvals applied to the in-process agent alone. Every `tools/call` now passes the same guardrail engine.
  `Confirm` raises a Human-in-the-Loop approval; once approved, the identical call runs once within
  10 minutes. **Behaviour change:** under the default policy, write tools called by external clients now
  need an approval. If that is too strict, adjust the active guardrail preset (autonomous level,
  confirmation for writes) — the preset applies to the agent as well.

### Added
- **MCP `serverInfo`.** `initialize` now reports name, title, version, description, website and icon, so MCP
  clients and directories show what the server is instead of the bare assembly name.
- **`lhm.plugin.json`**: the LobeHub Marketplace manifest (owner declaration), generated with
  `lhm plugin init` from the running server — 86 tools, 2 resources, 4 prompts.
- **MCP prompts and resources.** Four user-invokable prompts (`triage_unhealthy_containers`,
  `security_review_server`, `plan_safe_update`, `investigate_host_load`) package common operations workflows,
  with a built-in stop before any write. Two read-only resources describe the MCP surface:
  `whiskers://tools/catalog` and `whiskers://me/permissions` (the caller's own scope and callable tools).

### Changed
- **Remaining ServerWatch names cleaned up.** README examples use the `whiskers` MCP server key and
  `/whiskers` paths; `WHISKERS_SELF_CONTAINERS` replaces `SERVERWATCH_SELF_CONTAINERS` (the old name is
  still read) and its default now covers both `whiskers` and `serverwatch`; the safe guardrail default
  protects `whiskers*` as well as `serverwatch*`. Names that existing installs depend on — the compose
  service/volume `serverwatch`, Prometheus `serverwatch_*` metrics, the Data Protection application name,
  the mTLS client CN, the Trivy cache volume and the host-shell label — stay unchanged on purpose.
- **Architecture rules moved to `docs/ARCHITECTURE.md`.** Application shape, security-critical areas,
  protected layers and engineering rules now live there. `CLAUDE.md` is no longer tracked.

### Fixed
- The Helm chart's fallback `version`/`appVersion` still read 0.12.1; releases stamp both from the tag, but
  directories that read the file showed a stale version.

## [0.14.1] — 2026-10-25

Security fix in Settings: the role table could turn whitelisted addresses into Admins on the next save.

### Fixed
- **Settings → User roles could hand out Admin by accident.** Whitelisted addresses without a role entry were
  merged into the role table as Admin. The server gave them the default role, but the next save of *any* role
  wrote those phantom Admin rows to `roles.json` — also for addresses deleted from the role list earlier. The
  table now shows only explicit entries; whitelisted addresses without one are listed below it with the role
  they really get, and a click on one starts assigning a role on purpose.
- **Saving roles reset the default role** to Viewer, because the page did not carry it through.
- **Creating or deleting an MCP API key discarded unsaved "active" toggles** of the other keys. Pending toggles
  now survive the reload.
- **Deleting an MCP API key had no admin check and no audit entry.** It now requires Admin, and both create and
  delete are written to the audit log (`mcp.key.create` / `mcp.key.delete`, never the key value).

## [0.14.0] — 2026-10-25

AutoPatch, measured update risk, DNS records over MCP — and a full pass over the UI for buttons that claimed
success without checking, filters that did not filter and pages that crashed on empty data.

### Fixed
- **Copying an API key (or any other value) silently did nothing over plain HTTP.** `navigator.clipboard` exists
  only in a secure context, the error was swallowed and the page reported "copied" anyway. Copying now falls back
  to a selection copy, reports the real outcome, and when it fails it reveals the value and says to copy it by hand.
- **The image update check never saw stale containers.** It read the local digest through the tag, so any pull
  without a recreate made an outdated container look current — and once the tag moved, Docker listed the container
  by its bare image id and the registry was asked about that id ("could not reach registry"). The check now
  compares the registry with the image the container actually runs.
- **Container detail:** for a Viewer the tab positions shifted, so the database tab spun forever while the hidden
  env tab's loader ran instead; buttons for Admin-only database and `.env` actions were shown to roles the handler
  then refused; saving `.env` after opening a secret for editing without typing wiped the secret; the CVE tab of a
  notification deep link always said "no scan".
- **Adding a server twice.** Save → failed connection probe → corrected host → Save again created a second entry
  with the same id. A retry now updates the first entry, and the store refuses duplicate ids.
- **Renaming the active guardrail preset silently activated a different one** (the first in the list).
- **The setup wizard accepted a password the account creation then rejected** — on the last step, in Identity's
  English wording. The admin step now checks the real password policy and names it; the "local Docker host
  connected" step actually probes the host.
- Pages that ignored their own controls or failures: "Scan now" on the CVE page never refreshed it; health-report
  filters applied only after Refresh; the notification log could not filter most event types; the self-status
  timeline never refreshed; App Store port conflicts were computed for the first server, not the chosen target;
  the container diff read env from the default server; duplicate ports or variables in the deploy form crashed
  the page; a failed agent start left the chat busy for good; agent-mode chats in the floating widget were never
  saved; terminals printed "session ended" twice; scheduled tasks could be created without a target or with an
  invalid cron expression.
- Kubernetes servers are no longer offered as targets for compose deploys, the compose editor, the App Store or
  volume backups.
- Cloud power and snapshot actions are now written to the audit log, like every other host action.
- English UI showed German in the notification bell and the DNS settings panel.

### Fixed
- **A server stayed paused until the next restart after a probe that produced no verdict.** When the half-open
  probe of the per-server circuit breaker ended in an application error or was cancelled, nothing cleared the
  probe flag, so every later caller was refused and the server looked throttled forever. The probe is now
  released on every exit path; a transport failure still re-opens the circuit as before.
- **Restarting Whiskers briefly cut the whole tailnet when the host already runs Tailscale.** With
  `VPN_PROVIDER` unset, the entrypoint started a second `tailscaled` inside the host-network container, which
  made the host daemon restart and dropped every mesh connection for one to two minutes. The entrypoint now
  skips its legacy bring-up when `tailscale0` already exists (`VPN_FORCE_LEGACY_TAILSCALE=1` restores it).
- **The second server to go down within the throttle window was never reported.** Server-level events carry no
  container id, so every channel throttled them on one shared key — exactly when a mesh outage takes several
  hosts down at once. The key now includes the server, and reachability transitions are never throttled (the
  tracker already announces each outage once and closes it with exactly one recovery).
- **A background loop that ran on time and failed every single cycle was reported as healthy.** The
  supervisory rule fell back to the last *attempt* when a loop had never succeeded, so a permanently failing
  loop kept resetting its own age to zero and never triggered — the exact shape of the 2026-08-26 incident,
  where the thing that ran on schedule and achieved nothing was the thing nobody noticed. A loop with no
  successful cycle is now judged by how many chances it has had (three), not by how recently it tried; a
  freshly started process still gets those three cycles, so a restart is not an incident.
- **`/metrics` produced comma decimals for German-speaking clients.** The endpoint sits behind request
  localization, so a scraper or browser sending `Accept-Language: de` received `0,120` instead of `0.120` —
  and Prometheus rejects an entire scrape at the first unparsable line, which would have taken the monitoring
  dark because of a request header. The endpoint is now pinned to the invariant culture for the duration of
  the request. This affected the pre-existing container metrics too, not only the new self-metrics.

### Added
- **Dashboard: the "Stopped" and "Unhealthy" tiles filter.** One click shows exactly the containers the tile
  counts, across all servers (combined with the group/tag filter); a chip or a second click clears it.
- **AutoPatch — nightly same-tag security patching (opt-in, off by default, dry run by default).** Rolls a
  container onto the newest image of its own tag through compose: never across a major version, skipped when the
  compose file has drifted from the running container or the image is built on the host, databases dumped and
  verified first, health-checked afterwards with automatic rollback. `get_auto_patch_status` and
  `preview_auto_patch` show what it would do.
- **Measured update risk.** `assess_update_risk` pulls the candidate image and compares it with the running one;
  `get_blast_radius` lists what a change takes down with it.
- **DNS records over MCP** (module `dns`, Infomaniak): `list_dns_records`, `set_dns_record` (idempotent),
  `delete_dns_record`, restricted to allowed zones.
- **borg-fleet** (`deploy/borg-fleet`): nightly append-only backups of every server to a storage box, one
  sub-account per server.
- **`renew_mtls_certificate` renews a proxy certificate without locking anyone out.** The host generates a new
  key, step-ca signs the CSR, and ghostunnel restarts with it. Before the swap the host arms a three-minute timer
  that restores the previous certificate unless Whiskers confirms through the new one. Admin-only, audited.
- **Warnings before a certificate expires.** Whiskers now watches its mTLS client certificate and CA bundle, the
  certificate of every mTLS proxy and certbot's certificates on all servers. It notifies 30, 14 and 7 days before
  expiry and once more on expiry (`certificate_expiring` / `certificate_expired`). An expired client certificate
  or CA used to take every SSH-free server down at once with no warning beforehand.
- **The generic outbound webhook can sign its requests.** With a secret set (Settings → generic webhook) every
  POST carries `X-Webhook-Timestamp` + `X-Webhook-Signature-V2` (HMAC-SHA256 over `<timestamp>.<body>`, so a
  captured request cannot be replayed) and the GitHub-style `X-Hub-Signature-256`. Receivers that require a
  signature — e.g. a Hermes agent webhook route that forwards alerts to Telegram — now accept Whiskers alerts.
  The payload also names the server (`server`, `serverId`).
- **"Did the automatic actions help?" is now a section on `/self-status`**, directly above the timeline that
  says what Whiskers did. A hit rate is shown only once at least five outcomes have been judged — "67%" from
  three attempts reads as a measurement and is a coin toss — and it is marked unreliable when more outcomes
  could not be measured than could, because such a number describes the checking rather than the actions.
  Unmeasurable outcomes never enter the rate and are never left out of the table either: a gap that is not
  shown is coverage the page would be silently claiming. Rows are ordered by how much attention they need,
  since somebody opening the page is asking "is any of this working?" and a good rate is not an answer to
  that.
- **Automatic actions are now checked against a promise made before they run.** Until now an action counted
  as successful when the call returned without an error — not when the problem went away, which is the
  incident's own confusion one level up. Every automatic action kind must declare how it can be checked
  (metric, direction, threshold, window); one without a criterion fails the test run and cannot be recorded as
  done. Afterwards the window is judged against series that already exist, and the verdict is one of three:
  it worked, it changed nothing, or **it could not be measured**. That third verdict is never folded into the
  first — missing data read as success is precisely the failure this whole package is about. The three
  existing self-throttles are wired in, and for those "changed nothing" means something sharp: the load was
  never ours, and monitoring was taken off a server for no benefit. `get_action_outcomes` reports the hit
  rates. **MCP clients must reconnect to see it.**

  Rollback and repeat-locking are deliberately **not** switched on: the plan asks for four weeks of
  observation first, and enabling them before anyone knows whether the criteria are any good would be the
  same habit this package exists to break.
- **A rolling baseline per host — and a guard for when the baseline learns the fault.** Each server now has
  an exponentially weighted mean and deviation for its own CPU, so a host behaving unlike itself is reported
  even when no fixed threshold is breached. Four sigma rather than the textbook three, because host CPU has a
  floor, a ceiling and a long tail of legitimate busy periods, and three sigma on that shape alerts most days.

  The important half is the guard. A baseline that keeps learning through an incident eventually decides that
  98% is normal and stops complaining — going quiet precisely when the problem has lasted longest. On the
  incident bench the deviation alert is raised eleven minutes after the step and clears again within the
  hour; anyone reading that all-clear alone would conclude the server had recovered. So the rule watches its
  own mean, and reports when the learned normal itself crosses the fixed threshold — 19.7 hours into the
  incident, two days earlier than the plan asked for. This is the third time this codebase has produced a
  measure that adjusts to what it is meant to detect, which is why four of the nine tests cover this one
  behaviour.
- **A Docker daemon that has become slow is now a signal of its own.** Whiskers makes hundreds of API calls a
  minute and never timed one of them: an overloaded daemon was visible only as things "feeling slow". The
  fleet listing — one call per server per health cycle — is now timed, and a recent median three times the
  host's own baseline raises an alert. Deliberately a ratio, not a millisecond threshold: a Pi over a tunnel
  and a local socket differ by an order of magnitude while both are healthy. Only successful calls are
  measured, because a call cut off at its timeout says "at least 8 seconds" and would peg the median the
  moment a host went fully silent — a case the circuit breaker already covers far better. The rule detects
  the *transition*; once a host's new normal is slow, the sustained state is the host-CPU rule's job.
- **Host alerts close themselves.** A server that goes back to normal now says so, with its own event type
  and informational severity — an all-clear delivered under the alarm's own name would be rendered red, next
  to a warning icon, and read as a fresh incident. The all-clear waits until the value is five points below
  the threshold and has stayed there five minutes: without that margin a host sitting at 87% would be
  declared recovered while still nearly saturated, and one grazing the threshold would flap until somebody
  muted the channel. An open finding is escalated only when it gets materially worse, never repeated. The
  count of unclosed findings and the age of the oldest are on `/metrics` — a number that only ever climbs
  means the closing path is broken, not that the problems are patient.
- **A server pinned at 98% CPU is now reported — the gap the 2026-08-26 incident fell through.** Alerts were
  evaluated per container plus one server-level rule for disk, and `dockerd` runs in no container: Whiskers
  recorded roughly 8,900 measurements of that host over six days, practically every one above 98%, and judged
  none of them. Sustained host CPU and memory are now evaluated, and so is the more specific signal — host
  load that **no container accounts for**, which names the class of cause instead of only the symptom.

  Comparing those two numbers means reconciling two conventions: host CPU is a percentage of the whole
  machine, container CPU is Docker's, where one busy core is 100 and a 2-core host reaches 200. Skipping that
  conversion fails silently in the dangerous direction — the unexplained gap comes out too small and the
  alert never fires — so it is pinned by a test on a 4-core host, a case the incident's own numbers do not
  expose. The rules are driven by sample time rather than the wall clock, which lets a reconstruction of that
  week replay through them in under a second and report 20 August, 14:14 instead of six days later. The new
  read-only `get_host_load` MCP tool answers the same question for the agent — per-container stats never
  could, since `dockerd` appears in none of them. **MCP clients must reconnect to see it.**
- **"What has been happening" on `/self-status`** — the last six hours of fleet-changing actions in one list:
  deploys, restarts, rule changes, agent actions, and Whiskers' own decisions (a pause, an open circuit, a
  suspended log scan) side by side, because those change the numbers with nobody having touched the fleet.
  Everything is computed in UTC and converted only at the point of display: a timeline with an offset in it
  is worse than none, since it will confidently suggest that the thing which happened *after* the spike
  caused it. Read-only actions are filtered out by excluding read verbs rather than by listing write ones, so
  a newly added kind of intervention shows up by default instead of being silently missing.
- **Containers whose logs are not being read are now listed, with the two reasons kept apart.** A container
  the scan gave up on after repeated timeouts and one deliberately excluded as Whiskers' own access path look
  identical from outside — no findings — and mean opposite things: a fault it will retry versus a decision.
  Both appear on `/self-status` under one honest heading, "Containers not being read", with distinct labels
  and faults listed first. Shown on separate pages, an operator would have to know both existed to be sure a
  container was covered at all.
- **A "Whiskers about itself" page** at `/self-status`: per loop and server, how long ago it last completed a
  cycle (as an age, never a timestamp — a clock time makes the reader do the subtraction, and doing it wrong
  is how a six-day-old failure goes unnoticed), the cycle duration, failures, and which servers a loop
  deliberately skips. Plus the per-server load budget, circuit state, and whether background checks are
  paused. The verdict comes from the same threshold the alert uses, read from it rather than copied: a page
  with its own quietly different rule would look authoritative while contradicting the alert that woke
  someone up.
- **Whiskers' own loop health survives a restart, and the agent can ask about it.** The self-metrics are
  written to a new `SelfMetricSamples` table once a minute (additive migration, both providers) and restored
  on boot. The history is the smaller half of the point: after a restart the in-memory view is empty, and an
  empty "last success" is indistinguishable from "never succeeded" — so the supervisory rule would have had to
  either alarm on every restart or stay quiet about fresh loops, and the second is exactly the window in which
  a bad deploy has most likely broken something. A live reading always beats the stored one, nothing older
  than a week is restored, and there is a test specifically for the direction that matters most: a restart
  must not make a genuinely stalled loop look alive. Sampling costs zero Docker calls, which is pinned by a
  test. The new read-only `get_whiskers_self_status` MCP tool reports the same thing in prose — including
  whether an absence of findings can be taken at face value. **MCP clients must reconnect to see it.**
- **Container logs growing without a rotation limit are now reported before the disk fills.** A daily survey
  reads each container's log driver configuration and the size of its log file, works out the growth per day
  from consecutive readings, and judges it **against the free space on that host** — 150 MB is a footnote next
  to 10 GB of headroom and an alert next to 200 MB, so an absolute threshold would be wrong nearly everywhere.
  A size that cannot be read is reported as *unknown*, never as zero and never estimated. The alert carries
  the compose snippet, the exact recreate command for that container, and the `daemon.json` default that
  stops it happening to the next one — and says plainly that applying it recreates the container. It never
  runs anything. Findings and exclusions are both in the `get_log_hygiene_report` MCP tool. Every message
  states that this removes the *trigger* of the 2026-08-26 incident and not its cause, so that closing this
  ticket does not read like closing the incident.
- **The log scan no longer reads the record of its own traffic.** The two containers that triggered the
  2026-08-26 incident were the tunnel and socket proxy Whiskers reaches Docker through: every request it makes
  is a line in their logs, and in two weeks those logs reached 822 MB. Containers on the access path are now
  detected — by matching the address and port Whiskers actually connects to against what the container
  publishes, never by name — and skipped by the log-alert scan only; health, metric and CVE monitoring still
  cover them. `SERVERWATCH_SELF_CONTAINERS` keeps working and takes precedence. Every exclusion is visible in
  the new `whiskers_log_scan_exclusions` metric and the read-only `get_log_hygiene_report` MCP tool, because
  a container excluded by mistake looks exactly like one with nothing to report. This removes the *trigger*
  of that incident, not its cause — the cause was a log fetch that was abandoned rather than cancelled, and
  both the alert text and the report say so. **MCP clients must reconnect to see the new tool.**
- **An emergency stop for Whiskers' own background checks.** During the 2026-08-26 incident the load on the
  host *was* Whiskers, and the only way to stop it was SSH on the affected server — past the tool causing the
  problem. Background checks for a single server can now be paused, from the UI-facing service or over MCP
  (`pause_server_checks`, `resume_server_checks`, `list_paused_servers`). The check sits at the one point every
  Docker call passes through, so a background loop cannot miss it; interactive access keeps working, because
  an operator pauses a server in order to look at it. Agents may pause for at most 120 minutes and must state
  a reason — an open-ended pause is a decision about how much blindness is acceptable, and that stays with a
  person. **MCP clients must reconnect to see the new tools.**

  The switch is built with its own failure mode in mind: a pause that outlives its reason is a server nobody
  is watching and nobody remembers deciding not to watch. So every pause is announced when set and when
  lifted, expires on its own, is not carried across restarts, and — while it stands — is re-reported every 24
  hours. The rule that reports the *absence* of checks cannot be paused by it; that separation is enforced by
  a test rather than by convention.
- **The agent can now see deploys, backups and its own alert history.** Four read-only MCP tools closed real
  blind spots: `list_git_deploy_apps` (repo, branch, outcome of the last deploy), `list_volume_backups` and
  `list_volumes` (answering "when was this volume last backed up?", with the age spelled out), and
  `list_recent_alerts`. The last one mattered most — the agent could read container states and raw logs but
  not the conclusions Whiskers had already drawn, so it re-derived them or missed them entirely. Their write
  counterparts are deliberately absent: starting a deploy belongs with a post-deploy health check and
  automatic rollback, restoring a volume overwrites live data, and an agent that can send notifications can
  flood the one channel that has to stay trustworthy. **MCP clients must reconnect to see the new tools** —
  connectors read the tool list once, at session start.
- **Every MCP tool now declares its permission level in its own source**, via `[McpToolLevel]` next to
  `[McpServerTool]`, and the served surface is pinned in a generated
  [tool catalog](docs/mcp-tool-catalog.md). Levels were carried over unchanged — this adds enforcement, not
  policy.
- **A server that stops answering now raises an alert.** A host dropping off the fleet was silent: the
  dashboard marked it unreachable, but nothing was sent — and every container alert and log-alert rule
  covering that host quietly stopped producing anything, which looks exactly like "all quiet". The health
  monitor now emits `server_unreachable` after two consecutive failed cycles
  (`HealthMonitor:ServerUnreachableCycles`) and `server_recovered` when the host is back. Both are
  rendered by every channel, link to `/servers`, and can drive an AI trigger. A server that has not
  answered once since startup gets a longer grace (`ServerUnreachableColdStartCycles`, default 10 cycles),
  because remote connections need a moment after a restart — without it a six-server fleet produced ten
  notifications per deploy.
- **The alert history is finally written.** `AlertHistory` has existed since the first migration but
  nothing ever wrote to it, so "what fired last week, and on which host?" was unanswerable. Every
  delivered notification is now recorded with its server, container, type and message; a
  `server_recovered` closes the outage rows it ends. Retention is the existing hourly prune.

### Fixed
- **Whiskers held a two-core host at 98% CPU for six days — its own doing.** The log monitor bounded each
  fetch with `Task.WhenAny(fetch, Task.Delay(timeout))`, which ends the *wait* and leaves the *request*
  running: dockerd kept reading the log file until the proxy cut the connection 600 seconds later, while a
  fresh fetch started every 60-second cycle. Ten concurrent full-log scans per container was the stable end
  state (1.15 million `read()` syscalls per second, `dockerd` at 184% of 200%). The cancellation token now
  reaches the Docker calls *and* the multiplexed read loop, so an expired deadline ends the request on the
  server too. Two more places bounded a per-server call the same abandoning way — the fleet-wide container
  listing and the system-info probe, both of which run for every server on every cycle — and were converted
  as well. Full analysis in [the incident report](docs/reviews/2026-08-26-logmonitor-dockerd-cpu-incident.md);
  the invariants that would have caught it now run in the test suite.
- **Nothing limited how much Whiskers asked of one server.** Five background loops each policed only
  themselves with their own timeout; the server sees the sum. There is now a per-server load budget at the
  point Docker calls pass through, with separate lanes for background work and for anything a person is
  waiting for — a background scan can no longer make the UI look frozen. Alongside it a circuit breaker:
  after a run of transport failures Whiskers stops calling that host and retries with a single probe after a
  cooldown. **Every such self-throttling is announced** (`server_throttled` / `server_throttling_ended`),
  because a pause nobody is told about is indistinguishable from "all quiet" — which is what let the incident
  above run for six days.
- **A forgotten permission entry made a tool look present and never work.** `McpPermissionCheck` resolves a
  tool that is missing from `DefaultToolLevels` to `admin` — correct as a fail-closed default, but it meant a
  forgotten entry produced a tool that is registered, appears in `tools/list`, and is then denied to the
  in-process agent on every single call, with no error and no log line. Levels are now declared on the method
  and the build fails on any drift, including a stale dictionary entry for a tool that no longer exists, a
  misspelled name in the permission gate, and a wire name that stops matching the method.
- **The tool-registration guard only checked a lower bound.** It asserted "more than 40 tools", which stays
  green while an entire module falls out of the catalog and its tools vanish from the served surface — the
  shape of the regression that left the shipped MCP server serving nothing from 0.12.0 to 0.13.0. Counts are
  now pinned per module, and a new test boots the real application and asks the running server for its tool
  list over the real endpoint. Reintroducing the original bug was verified to fail exactly that test, and
  none of the others.
- **Log alerts only ever watched one server.** The monitor's scan listed containers without a server id,
  which returns the **default** server's containers only — so every "on all containers" rule silently
  covered a single host while the other five in a six-server fleet were never read. The scan now uses the
  fleet-wide container list and fetches each container's logs from its own server. Pulled along with it:
  log watermarks and rule cooldowns are keyed by server + container id (container ids are unique per host
  only), hosts are scanned in parallel with a bounded per-fetch timeout, and the alert names the server and
  the line that matched.
- **A rule filtered to one container fired on all of them.** "No filter" was decided on `ContainerId`
  alone, but the UI dialog and the `create_log_alert` MCP tool only ever set `ContainerName` — so every
  container-specific rule degraded into an all-containers rule. Both fields are now considered. (Masked
  until now by the single-server scan; with a fleet-wide scan it would have meant an alert storm.)
- **An outage no longer swallows the log lines written during it.** An unreachable host returns an empty
  container list, which the monitors treated as "these containers are gone" and dropped their state for —
  re-baselining the host to "now" on recovery. Per-server state is now kept for hosts that did not answer,
  so the outage window is still scanned and a container that really stopped is still reported.
- **Log search searched one server too.** Without an explicit server it asked only the default host, while
  the page's container picker lists containers from every server — picking a remote container returned "no
  matches". Results now name the server they came from.
- **Muting a container silenced only a third of its alerts.** The per-container notification preferences
  were consulted by the health monitor alone; log alerts, CVE findings, image updates and metric alarms
  ignored them. The check now runs centrally for every producer.
- **Bounded log transfer.** With a `since` watermark the Docker call asked for *all* new lines, so one
  chatty container could pull its whole burst over a remote connection every minute, per cycle,
  fleet-wide. The tail limit (200 lines) now applies in both cases.
- The self-log guard that keeps Whiskers from alerting on its own log lines now applies only to the host
  Whiskers runs on. A container that merely shares its name on a remote host is a different process and
  stays monitored.

- **An AI trigger acting on a remote alert reported the container as missing.** Its tools take a server id,
  while everything it reads names the server ("Rabenhof (Hetzner)") — so it passed a name (or invented an
  id), got "Server not found" and concluded the container did not exist. The trigger's task message now
  states the server id explicitly, and the server lookup accepts a display name as well (ids win). Only
  surfaced once alerts started arriving from hosts other than the default one.

### Security
- **Onboarding no longer ships the proxy's private key across the network.** The key was generated on Badwolf
  and sent base64-encoded through the command channel. It is now generated on the new host; only the CSR and the
  signed certificate travel. Whiskers refuses any CSR that asks for more than the host's own name and IP, so a
  compromised host cannot obtain a certificate for another host.
- Matrix messages are built from an HTML-encoded copy of the event. The log-alert detail now carries the
  matched log line — third-party text that must not reach an HTML body unescaped.

## [0.13.1] — 2026-07-24

### Fixed
- **The MCP server exposed no tools.** The module-driven MCP registration (added in 0.12.0) passed the
  enabled modules' tool types as a `Type[]`, which binds to the generic `WithTools<T>(T target)` overload
  instead of `WithTools(IEnumerable<Type>)` and registers **zero** tools. As a result the server
  advertised only the `logging` capability and every client got `-32601 "Method 'tools/list' is not
  available."` — the entire tool surface was invisible over MCP. Fixed by passing the tool types as
  `IEnumerable<Type>`. Added `McpToolRegistrationTests` to guard the overload binding, since MCP tool
  serving previously had no test coverage.

### Added
- The running app version is shown in the sidebar, under the Whiskers wordmark.

## [0.13.0] — 2026-07-17

The governance story, end to end: every agent action now carries one correlation id from guardrail
through approval to history, a guided setup takes you from zero to a governed agent in four steps,
and the whole UI is available in English.

### Added
- **Full English UI + in-app handbook.** Every page, notification and the handbook are localized;
  English is the default and the app follows the browser language (switch anytime). The AI chat
  now answers in the user's language.
- **Guided "Secure AI Operations" setup.** A new admin-only **AI Operations** page walks you to a
  governed agent in four steps: choose how AI connects, create a read-only MCP key (shown once),
  pick one of three starter guardrail presets (*Observe only* / *Safe operations* / *Approval
  required*) with a full policy preview before you activate it, then try it and verify. It reuses the
  existing key and guardrail flows — no new secret handling.
- **End-to-end correlated governance chain.** Every agent tool call carries a stable correlation id
  from guardrail → approval → execution → history and notification, so one action reads as one thread.
  Approvals now show the *real* required permission level, a derived risk band, the target
  server/workload and the guardrail preset + rule that matched — on the approval card and in the
  call-detail dialog.
- **Server groups & tags.** Give each server an optional group and free-form tags; the dashboard
  gains a group/tag filter and the `list_servers` MCP tool takes an optional `tag` filter — quick
  ways to narrow a larger fleet.
- **Keyless-signed release images (cosign / Sigstore).** Release images are signed with the release
  workflow's GitHub OIDC identity and logged in Rekor — verify with `cosign verify` (see README →
  Security → Supply chain). Complements the existing Trivy gate, SLSA provenance and SBOM.

### Changed
- **The governance surfaces explain themselves.** Agent History, Audit Log, Approvals and Guardrails
  now lead with what they are for; the Approvals empty state spells out that Confirm genuinely pauses
  execution, and Guardrails opens with the allow/confirm/block framing.
- Documentation restructured around the governance positioning, with screenshots and a demo script.

### Fixed
- **CI never actually ran the test suite.** `Whiskers.Tests` was missing from the solution file, so
  `dotnet test --no-build` exited 0 without executing a single test — every green CI run before this
  release was build + boot-gate only. The project is now in the solution and the Test step fails on a
  silently-empty run.

### Upgrading
- Adds one additive, nullable migration (the correlation id on the MCP call log) for both SQLite and
  PostgreSQL. It runs automatically on first start — no action required.

## [0.12.1] — 2026-07-11

Security hardening plus the features that landed right after the 0.12.0 tag was cut.
The three security items below were previously listed under 0.12.0, but the published
0.12.0 artifacts were built before they landed — they ship starting with this release.

### Security
- **SSH host keys are now verified** (trust-on-first-use, pinned in
  `<data>/ssh-keys/known_hosts`). *Behavior change:* an intentionally rebuilt server needs
  its line removed from that file before reconnecting.
- **Fail-closed authorization**: every endpoint/page requires authentication unless it
  explicitly opts out (login, setup wizard, health probes, HMAC webhooks, token-gated
  metrics). The SignalR hub is no longer reachable anonymously.
- MCP bearer scheme is parsed case-insensitively (RFC 7235).

### Added
- **Git-based deployments** (`gitdeploy` module) — clone/pull a repository on a target
  server and bring it up with Docker Compose; deploy tokens are vault-only (surfaced to git
  via a 0600 `GIT_ASKPASS` file), and the new `git-deploy` webhook action enables
  push-to-deploy from CI.
- **Container registries (v1)** — manage private registries in Settings with vault-stored
  credentials; image pulls authenticate automatically by registry-host match.
- **Localized navigation & app chrome** (English/German) — all nav items and groups.
- Audit log now also covers scheduler and webhook management actions in the UI.
- CI on every push/PR (build, full test suite, DI boot gate); first bUnit component tests.

### Changed
- Dependency refresh: MudBlazor 9.7, YamlDotNet 18, Npgsql EF provider 10.0.3,
  MCP SDK 1.4.1, NCrontab 3.4; release pipeline moved to docker/* actions v4/v7.

## [0.12.0] — 2026-07-11

First **published** release: the container image (`ghcr.io/lupusmalusdeviant/whiskers`)
and the Helm chart (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) are built and
scanned by the release pipeline from this version on.

### Added
- **First-run setup wizard** — create the admin account in the browser; no `.env` needed.
  `VAULT_KEY` and the initial MCP key are generated/shown once in the wizard.
- **Local login** (ASP.NET Identity, email + password) alongside Google/OIDC; unattended
  admin seed via `WHISKERS_ADMIN_EMAIL` + `WHISKERS_ADMIN_PASSWORD_FILE`.
- **Self-backup & restore** of Whiskers' own data dir — optionally VAULT_KEY-encrypted
  (AES-256-GCM), crash-safe deferred-swap restore, schedulable backup task.
- **Kubernetes**: Helm chart for running Whiskers *on* a cluster (single-replica by design,
  restricted PodSecurity, PVC), and managing k3s/Kubernetes clusters *from* Whiskers —
  pods on the dashboard with owner grouping, logs, honest scale/rollout actions; kubeconfig
  stored encrypted in the vault. Least-privilege RBAC manifest in `deploy/k8s/`.
- **Module framework** — every feature area (CVE, agent, terminal, notifications, webhooks,
  cloud control, image updates, …) is a module, toggled via `Features:<id>:Enabled`.
- **PostgreSQL** as a second database provider next to SQLite (`WHISKERS_DB_PROVIDER`).
- **Release pipeline** — multi-arch image (amd64/arm64) gated by a Trivy CRITICAL scan,
  SBOM + provenance, GitHub Release with pinned compose files and `install.sh`.
- **Guided onboarding** — dashboard first-server guide, upfront Tailscale question,
  step-tracked onboarding with actionable errors and safe resume, production-readiness
  checklist in Settings.
- **Light mode** with dark/light/system toggle; auto-update **rollback** (snapshot-based);
  i18n groundwork (English default, German complete for migrated pages).

### Changed
- **Webhook secrets are mandatory** (HMAC `X-Hub-Signature-256` over the raw body).
  *Breaking:* pre-existing webhooks without a secret are disabled at boot (not deleted) —
  regenerate their secret in the UI and update the CI caller, then re-enable.
- Fresh installs no longer seed the "local" Docker server when no Docker socket is present
  (Kubernetes deployments start with an empty fleet).

### Fixed
- The webhook UI test button now sends a genuinely signed request.
- `DockerService` split into focused internals (no behavior change); numerous smaller fixes.

## [0.11.0] and earlier

Pre-publication development (dashboard, MCP server + acting agent with guardrails and
approvals, CVE scanning with dedup/age tracking, zero-SSH mesh+mTLS onboarding, cloud
control for Hetzner/Hostinger, terminal, deployments, notifications, audit log, …).
History: `git log v0.12.0` and `docs/reviews/`.
