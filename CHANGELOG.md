# Changelog

All notable changes to Whiskers are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer
(0.x = pre-1.0, minor bumps may contain breaking changes — noted explicitly).

## [Unreleased]

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
