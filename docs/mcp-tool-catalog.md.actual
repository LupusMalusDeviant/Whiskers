# MCP tool catalog

The tools this Whiskers build serves over MCP, with the permission level each one requires.
A level is declared on the tool method via `[McpToolLevel]`; the request path enforces it through
`McpPermissionLevels.DefaultToolLevels`, and tests keep the two identical.

**This file is generated.** Do not edit it by hand — change the tools, then regenerate.
`McpToolCatalogSnapshotTests` fails the build whenever it drifts from the code, so a change to the
served surface is always a deliberate, reviewable diff.

Tools of a disabled module are not served. `read` < `write` < `admin`; a caller's key level must
reach the tool's level, and the in-process agent is additionally capped by its trigger.

## Module `agent`

| Tool | Level | Description |
|---|---|---|
| `instruct_agent` | read | Instruct the Whiskers agent to carry out an operations task described in natural language. The agent plans and executes using Whiskers's tools, but runs with YOUR permissions and the configured guardrails — it can never exceed your rights or bypass the guardrails. Returns the agent's final answer plus a short log of the tools it ran. |

## Module `all-in-one`

| Tool | Level | Description |
|---|---|---|
| `add_firewall_rule` | admin | Add a firewall rule (UFW) to allow or deny traffic on a port. |
| `backup_database` | write | Create a database backup (dump) for a database container. |
| `connect_container_to_network` | admin | Connect a container to a Docker network. |
| `create_network` | admin | Create a new Docker network. |
| `deploy_app` | admin | Deploy a new application on a server using a standardized template. Supports common app types like web apps, databases, and custom Docker images. Creates the container with sensible defaults and starts it. |
| `deploy_compose` | admin | Deploy an application using a docker-compose.yml content string. Creates and starts all services defined in the compose file. |
| `detect_database` | read | Detect the database type of a container. Returns the database engine (PostgreSQL, MySQL, MongoDB, Redis, Neo4j) or 'None'. |
| `disconnect_container_from_network` | admin | Disconnect a container from a Docker network. |
| `execute_command` | admin | Execute a shell command on a server. Use with caution. |
| `execute_query` | admin | Execute a SQL query or database command and return the results. |
| `get_action_outcomes` | read | Report whether Whiskers' automatic actions actually worked: per action kind, how many were effective, how many changed nothing, and how many could not be measured at all. Read this before repeating an action — a kind with a poor hit rate is one where the cause probably lies elsewhere. A high 'not measurable' count means the checking itself has broken down, which is NOT the same as things being fine. Read-only. |
| `get_container_details` | read | Get detailed information about a specific Docker container including its configuration, ports, labels, and stats. |
| `get_container_env` | read | Get environment variables of a running Docker container. Sensitive values (keys, secrets, passwords, tokens) are masked for security. |
| `get_container_logs` | read | Get logs from a Docker container. |
| `get_container_metrics` | read | Get historical CPU/memory metrics for a container over a time period. |
| `get_health_summary` | read | Get a health summary of all containers across all servers. |
| `get_host_load` | read | Report each server's current host load and how much of it the containers actually account for. Use this when a host looks busy but no container explains it — that gap means a process outside the containers (dockerd, a backup job, a runaway service) is the likely cause, which per-container stats can never show. The two CPU figures use different conventions and are reconciled here. Read-only. |
| `get_nginx_config` | read | Get the Nginx configuration for a specific site. |
| `get_schema` | read | Get the schema (columns, types, keys) of a table. |
| `get_server_info` | read | Get detailed system information for a server (OS, CPU, RAM, disk, Docker version, containers). |
| `get_server_logs` | read | Get system logs from a server via journalctl. Can filter by service name. |
| `get_server_metrics` | read | Get historical CPU/memory metrics for a server over a time period. |
| `get_update_status` | read | Check which containers have image updates available. |
| `get_whiskers_self_status` | read | Report whether Whiskers itself is still working: for every background loop and server, how long ago it last completed a cycle, how long a cycle takes, how many failed, and which servers it deliberately skips and why — plus the per-server load budget and circuit-breaker state. Use this before trusting an absence of findings: a loop that has stopped produces no alerts at all, which looks exactly like a fleet with nothing wrong. Read-only. |
| `list_containers` | read | List all Docker containers across all configured servers. Returns container name, image, state, health, server, and compose project. |
| `list_databases` | read | List all databases in a database container. |
| `list_firewall_rules` | read | List firewall (UFW) rules on a server. |
| `list_networks` | read | List all Docker networks on a server. Shows name, driver, scope, subnet, and connected containers. |
| `list_nginx_sites` | read | List Nginx sites (enabled and available) on a server. |
| `list_paused_servers` | read | List servers whose background checks are currently paused, with the reason, who paused them (an operator or Whiskers itself), and when the pause expires. A paused server produces no health, log, metric or CVE findings — silence from it means nothing is being looked at, not that nothing is wrong. |
| `list_servers` | read | List all configured servers with their connection type and status. Optionally filter by a group or tag (case-insensitive) to narrow a large fleet. |
| `list_ssl_certificates` | read | List SSL/TLS certificates managed by certbot on a server. |
| `list_systemd_services` | read | List systemd services on a server. |
| `list_tables` | read | List tables in a database with row counts and sizes. |
| `manage_systemd_service` | admin | Manage a systemd service (start, stop, restart, enable, disable). |
| `pause_server_checks` | write | Pause Whiskers' own background checks (health, logs, metrics, CVE, image updates) for one server, for a bounded number of minutes. Use this when Whiskers itself is the load on a host. Interactive access keeps working, so the server can still be inspected. The pause is announced, expires by itself, and is reminded about if it outlives its reason. This does NOT stop the containers on that server and does NOT block anything running there. |
| `remove_firewall_rule` | admin | Remove a firewall rule by its number. |
| `remove_network` | admin | Remove a Docker network by name or ID. |
| `renew_mtls_certificate` | admin | Renew the mTLS certificate of a server's Docker proxy (ghostunnel). A new key is generated on the host, step-ca signs it, and the proxy restarts with it. If Whiskers cannot reach the host with the new certificate, the host restores the previous one by itself after 3 minutes. Admin only. |
| `renew_ssl_certificate` | write | Renew an SSL certificate using certbot. |
| `restart_container` | write | Restart a Docker container. |
| `resume_server_checks` | write | Resume Whiskers' background checks for a server that was paused. Safe to call even if it is not paused. Turning monitoring back on is never the dangerous direction, so this has no time limit. |
| `set_container_env` | write | Set environment variables in a container's .env file and restart via docker compose. Only works for containers managed by docker-compose. Provide variables as 'KEY=VALUE' pairs separated by newlines or commas. Existing variables not included are kept unchanged. |
| `start_container` | write | Start a stopped Docker container. |
| `stop_container` | write | Stop a running Docker container. |
| `update_container` | write | Pull latest image and recreate a Docker container (update). |
| `update_nginx_config` | admin | Update an Nginx site configuration. Validates with nginx -t before applying. |

## Module `cloud-control`

| Tool | Level | Description |
|---|---|---|
| `cloud_create_snapshot` | admin | Create a snapshot of a server (by Whiskers name or id) via its cloud provider. Useful before risky changes. Note: Hostinger keeps only ONE snapshot per VM (replaces the previous). |
| `cloud_hard_reset` | admin | HARD reset (power-cycle) a server via its cloud provider — forceful, use only when a graceful reboot is impossible (e.g. SSH unresponsive). Hetzner: true power-cycle; Hostinger: falls back to a restart (no hard reset available). |
| `cloud_metrics` | read | Get recent cloud metrics for a server (by Whiskers name or id). Hetzner type: cpu, disk, network. Hostinger returns raw metric data. |
| `cloud_power_on` | admin | Power on a server (by Whiskers name or id) via its cloud provider. |
| `cloud_reboot` | admin | Gracefully reboot a server (by Whiskers name or id) via its cloud provider. |
| `cloud_shutdown` | admin | Gracefully shut down a server (by Whiskers name or id) via its cloud provider. |
| `cloud_status` | read | Get the live cloud status of a Whiskers server (by name or id): provider, power state, type, location, IP, and (Hetzner) traffic usage and backups. |
| `hetzner_change_server_type` | admin | Change (resize) a Hetzner server's type, e.g. 'cx32' (by Whiskers name or id). The server must be powered off first. upgradeDisk=true also grows the disk (then a downgrade is no longer possible). |
| `hetzner_delete_snapshot` | admin | Delete a Hetzner snapshot/image by its numeric ID, in the account of a given Whiskers server. Irreversible. |
| `hetzner_disable_backups` | admin | Disable Hetzner automated backups for a server (by Whiskers name or id). Existing backups are deleted. |
| `hetzner_disable_rescue` | admin | Disable Hetzner rescue mode on a server (by Whiskers name or id). |
| `hetzner_enable_backups` | admin | Enable Hetzner automated daily backups for a server (by Whiskers name or id). Adds ~20% to the server price. |
| `hetzner_enable_rescue` | admin | Enable Hetzner rescue mode on a server (by Whiskers name or id), then it must be reset to boot into rescue. Returns the temporary root password. Recovery when the OS won't boot. |
| `hetzner_list_snapshots` | read | List Hetzner snapshots in the account of a given Whiskers server (by name or id). |
| `list_cloud_servers` | read | List all Whiskers servers that have a cloud provider (Hetzner/Hostinger) configured, with their live power status, type, location, IP, and (Hetzner) traffic usage. |

## Module `cve`

| Tool | Level | Description |
|---|---|---|
| `get_container_cves` | read | Get the CVE findings for a specific container image on a server. |
| `get_cve_summary` | read | Get a CVE summary across all servers: per-server counts of CVE findings (OS + all containers) broken down by severity. |
| `get_server_cves` | read | Get the CVE findings for the host OS on one server (pending security updates and the CVE IDs they address). |
| `list_cve_groups` | read | List DE-DUPLICATED CVEs across the whole fleet: one entry per CVE-ID with every affected server/container behind it, how long it has been open, and whether a fix exists. Use this instead of the per-target tools to avoid duplicate CVEs. |

## Module `dns`

| Tool | Level | Description |
|---|---|---|
| `delete_dns_record` | write | Delete the DNS record(s) with the given name and type in a zone at the configured provider. Only A, AAAA, CNAME and TXT can be deleted. Answers what was removed; nothing there = no-op. |
| `list_dns_records` | read | List the DNS records of a zone (e.g. 'lupusmalus.dev') at the configured DNS provider. Names are relative to the zone ('@' = the zone itself). Optionally filter by name and/or type. Read-only. |
| `set_dns_record` | write | Create or update one DNS record at the configured provider — idempotent: an existing record with the same name and type is updated (never duplicated), and an identical value answers 'unchanged'. Types: A (IPv4), AAAA (IPv6), CNAME (hostname), TXT. Name is relative to the zone ('holler.app' → holler.app.<zone>, '@' = zone apex). Returns before/after. |

## Module `gitdeploy`

| Tool | Level | Description |
|---|---|---|
| `list_git_deploy_apps` | read | List the applications Whiskers deploys from Git: repository, branch, compose path, target server, and the outcome, time and commit of the last deploy. Read-only — this cannot start a deploy. |

## Module `image-updates`

| Tool | Level | Description |
|---|---|---|
| `assess_update_risk` | read | Assess what updating one container's image would change, BEFORE recreating anything. Compares what the running image and the candidate image declare — entrypoint, user, exposed ports, expected volumes, healthcheck, base OS — and counts which CVEs the update actually closes. Pulls the candidate image (starts and restarts nothing). Read-only with respect to running containers. |
| `get_auto_patch_status` | read | Report of the last security auto-patch cycle: which containers were rolled onto the newest image of their own tag, which were skipped and why (major version, floating tag without version, compose drift, excluded), and which were rolled back. Also shows whether auto-patching is on and when it runs. |
| `get_blast_radius` | read | Who else a change to this container touches: which containers get recreated with it, which depend on it, which it depends on, and whether the operation would cut the connection Whiskers is using to perform it. Reads compose labels only — no pull, no probe. Useful before any restart, update or stop, not just updates. |
| `preview_auto_patch` | write | Start a DRY RUN of the security auto-patch cycle in the background: for every container with fixable vulnerabilities it pulls the newest image of the same tag, runs every guard (major version, compose drift) and puts the old tag back. Recreates nothing, whatever the settings say. Read the result with get_auto_patch_status after a few minutes. |

## Module `logmonitor`

| Tool | Level | Description |
|---|---|---|
| `create_log_alert` | write | Create a log alert rule that triggers notifications when a pattern is found in container logs. Rules are evaluated on every configured server. |
| `get_log_hygiene_report` | read | Report the log-file situation on the fleet: which container logs are growing without a rotation limit (with size, growth per day, share of the free disk and the exact command to fix it), and which containers the log-alert scan steps over and why. Excluded containers are still covered by health, metric and CVE monitoring; only their log content is ignored. Read-only: this changes nothing and runs nothing — setting a rotation limit recreates the container, which is a person's decision. |
| `list_log_alerts` | read | List all configured log alert rules. |
| `search_logs` | read | Search container logs for a pattern (text or regex). Searches every server unless a serverId is given. Returns matching lines across one or all containers. |

## Module `notifications`

| Tool | Level | Description |
|---|---|---|
| `list_recent_alerts` | read | List the alerts and events Whiskers has raised recently (container down, restart loops, log-alert hits, CVE findings, agent actions), newest first. Optionally filter by severity or event type. Read-only — this cannot send notifications. |

## Module `scheduler`

| Tool | Level | Description |
|---|---|---|
| `create_scheduled_task` | write | Create a new scheduled task (e.g., periodic backup, container restart, cleanup). |
| `delete_scheduled_task` | write | Delete a scheduled task by its task ID. |
| `list_scheduled_tasks` | read | List all scheduled tasks with their status, schedule, and last run info. |
| `run_scheduled_task` | write | Run a scheduled task immediately (outside its normal schedule). |

## Module `volumebackups`

| Tool | Level | Description |
|---|---|---|
| `list_volume_backups` | read | List the Docker volume backups Whiskers has taken: volume, owning container, server, size and age. Answers 'when was this volume last backed up?'. Read-only — this cannot create or restore a backup. |
| `list_volumes` | read | List the Docker volumes on a server, so a backup gap can be spotted by comparing this against list_volume_backups. Read-only. |

## Totals

| Level | Tools |
|---|---|
| read | 46 |
| write | 16 |
| admin | 24 |
| **total** | **86** |
