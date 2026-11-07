using System.Collections.Frozen;
using Whiskers.Models;

namespace Whiskers.Services.Mutations;

/// <summary>The single table of mutating capabilities (ADR-0005, INV-13).</summary>
public interface ICapabilityCatalog
{
    IReadOnlyCollection<Capability> All { get; }

    /// <summary>The capability, or <c>null</c> when the id is unknown — callers treat that as Deny.</summary>
    Capability? Find(string capabilityId);

    /// <summary>The capability an MCP tool performs, or <c>null</c> for read tools.</summary>
    Capability? FindByMcpTool(string toolName);
}

/// <summary>
/// The authoritative level table (R1-05, INV-13): every mutating capability states its level here, for the UI and the
/// MCP surface alike. <see cref="McpPermissionLevels.DefaultToolLevels"/> takes the level of every mutating tool from
/// <see cref="McpToolLevels"/>, and <c>CapabilityTableConsistencyTests</c> checks the tool attributes against it.
/// Where two surfaces disagreed, the stricter level won; root-equivalent operations are Admin.
/// </summary>
public sealed class CapabilityCatalog : ICapabilityCatalog
{
    /// <summary>The UI role that holds a catalog level, so pages take their requirement from this table (R1-05). This
    /// maps a <em>required</em> level: anything that is not read, write or admin is held to Admin (fail closed),
    /// unlike <see cref="McpPermissionLevels.Normalize"/>, which reads an unknown <em>granted</em> level as read.</summary>
    public static AppRole RoleFor(string level) => level?.Trim().ToLowerInvariant() switch
    {
        McpPermissionLevels.Read => AppRole.Viewer,
        McpPermissionLevels.Write => AppRole.Operator,
        _ => AppRole.Admin,
    };

    /// <summary>The one place the fixed rule turns a role into the level it holds (INV-13): Admin ↔ admin,
    /// Operator ↔ write, Viewer (and anything else) ↔ read. Every surface that maps a signed-in person's role to a
    /// level calls this; <c>PageRoleScanTests.The_role_level_rule_is_spelled_only_in_the_catalog</c> forbids a second
    /// spelling.</summary>
    public static string LevelFor(AppRole role) => role switch
    {
        AppRole.Admin => McpPermissionLevels.Admin,
        AppRole.Operator => McpPermissionLevels.Write,
        _ => McpPermissionLevels.Read,
    };

    private static readonly TimeSpan Short = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Capability> _byId;
    private readonly Dictionary<string, Capability> _byTool;

    public CapabilityCatalog()
    {
        var all = Definitions().ToList();
        _byId = all.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _byTool = all.Where(c => c.McpTool is not null).ToDictionary(c => c.McpTool!, StringComparer.Ordinal);
        All = all;
    }

    public IReadOnlyCollection<Capability> All { get; }

    public Capability? Find(string capabilityId) =>
        capabilityId is null ? null : _byId.GetValueOrDefault(capabilityId);

    public Capability? FindByMcpTool(string toolName) =>
        toolName is null ? null : _byTool.GetValueOrDefault(toolName);

    /// <summary>The level of every MCP tool that performs a capability, straight from the table below.</summary>
    public static IReadOnlyDictionary<string, string> McpToolLevels { get; } = Definitions()
        .Where(c => c.McpTool is not null)
        .ToDictionary(c => c.McpTool!, c => c.RequiredLevel, StringComparer.Ordinal);

    // After Short / Normal / Long: static fields initialise in order, and the definitions read them.
    private static readonly IReadOnlyDictionary<string, string> LevelById = Definitions()
        .ToDictionary(c => c.Id, c => c.RequiredLevel, StringComparer.Ordinal);

    /// <summary>The UI role a page requires before it runs <paramref name="capabilityId"/>, straight from the table
    /// below through <see cref="RoleFor"/> (INV-13): no page states its own role for a capability. An unknown id is
    /// held to Admin — fail closed; the gateway refuses it in any case.</summary>
    public static AppRole RoleOf(string capabilityId) =>
        RoleFor(capabilityId is not null && LevelById.TryGetValue(capabilityId, out var level) ? level : McpPermissionLevels.Admin);

    /// <summary>
    /// INV-19: the root-equivalent capabilities — every row of the table below marked
    /// <see cref="CapabilityRisk.RootEquivalent"/>: anything that runs a caller-chosen command on a host or in a
    /// container, opens a host or container shell, deploys compose / apps / Git, writes a container's <c>.env</c> and
    /// brings it up, runs arbitrary SQL, or decides who and what may do any of that. Each needs Admin on every
    /// surface; <see cref="RootEquivalentPerCall"/> names the two whose level the gateway resolves per call.
    /// <c>RootEquivalentAdminTests</c> pins the membership.
    /// </summary>
    public static IReadOnlySet<string> RootEquivalent { get; } = Definitions()
        .Where(c => c.Risk == CapabilityRisk.RootEquivalent)
        .Select(c => c.Id)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The root-equivalent capabilities whose catalog level is below Admin because what they do depends on the task:
    /// <c>scheduler.create</c> / <c>scheduler.run</c>. <see cref="SchedulerRequirementResolver"/> raises a call to
    /// Admin when the task runs a custom command (<c>host.exec</c>) — before the caller check and before an approval
    /// is requested — and an unrecognised task type or task id to Admin as well (fail closed).
    /// </summary>
    public static IReadOnlySet<string> RootEquivalentPerCall { get; } =
        new[] { "scheduler.create", "scheduler.run" }.ToFrozenSet(StringComparer.Ordinal);

    private static Capability Tool(string id, string tool, string level, string target, CapabilityRisk risk, TimeSpan deadline) =>
        new(id, level, target, risk, tool, deadline);

    private static Capability Ui(string id, string level, string target, CapabilityRisk risk, TimeSpan deadline) =>
        new(id, level, target, risk, null, deadline);

    private static IEnumerable<Capability> Definitions()
    {
        const string W = McpPermissionLevels.Write, A = McpPermissionLevels.Admin;
        const CapabilityRisk Rev = CapabilityRisk.Reversible, Dis = CapabilityRisk.Disruptive,
            Des = CapabilityRisk.Destructive, Root = CapabilityRisk.RootEquivalent;

        // Containers and images
        yield return Tool("container.start", "start_container", W, "container", Rev, Normal);
        yield return Tool("container.stop", "stop_container", W, "container", Dis, Normal);
        yield return Tool("container.restart", "restart_container", W, "container", Dis, Normal);
        yield return Tool("container.update", "update_container", W, "container", Dis, Long);
        // Contract 0006: `docker update --restart=no` before a container whose exposure was not decided is stopped. Only
        // composed (compose.deploy, gitdeploy.deploy, container.env_set), never decided on its own; not above them.
        yield return Ui("container.disable_restart", W, "container", Dis, Short);
        // R1-05: writes the compose .env (any key: image, command, bind paths) and runs `docker compose up -d` — the
        // same as the container page's .env save, root-equivalent on the host.
        yield return Tool("container.env_set", "set_container_env", A, "container", Root, Normal);
        yield return Ui("container.remove", A, "container", Des, Normal);
        yield return Ui("container.create", A, "container", Root, Long);
        yield return Ui("container.rollback", W, "container", Dis, Long);
        // Kubernetes rows on the Dashboard (start / stop = scale the controller, restart = rollout restart).
        yield return Ui("workload.start", W, "workload", Rev, Normal);
        yield return Ui("workload.stop", W, "workload", Dis, Normal);
        yield return Ui("workload.restart", W, "workload", Dis, Normal);
        // Container page: save the compose .env and run `docker compose up -d` (root on the host).
        yield return Ui("container.env_file_save", A, "container", Root, Normal);
        // Container page: a changing command inside the container (Artisan migrate / rollback).
        yield return Ui("container.exec", A, "container", Root, Normal);
        // Web terminal (R1-17, PC-D2): opening a shell in a container or on a host; the close is audited by the
        // terminal manager, what is typed is not recorded.
        yield return Ui("terminal.container_open", A, "container", Root, Short);
        yield return Ui("terminal.host_open", A, "host", Root, Short);
        yield return Ui("image.pull", W, "host", Rev, Long);
        // R1-48: assessing an update pulls the candidate image on the host — the tag then points at it — so the tool
        // that was Read is decided as a write on the host it pulls on (it recreates and starts nothing).
        yield return Tool("image.assess_update", "assess_update_risk", W, "host", Rev, Long);

        // Deployments
        yield return Tool("app.deploy", "deploy_app", A, "host", Root, Long);
        yield return Tool("compose.deploy", "deploy_compose", A, "host", Root, Long);
        // Brokkr provisioning (contract 0001): saving an app over MCP creates one; its token has its own capability,
        // because a capability names one MCP tool.
        yield return Tool("gitdeploy.save", "create_git_deploy_app", A, "git-deploy-app", Root, Short);
        // A token decides which repository content the next deployment can read.
        yield return Tool("gitdeploy.set_token", "set_git_deploy_token", A, "git-deploy-app", Root, Short);
        yield return Ui("gitdeploy.delete", A, "git-deploy-app", Rev, Short);
        yield return Ui("gitdeploy.deploy", A, "git-deploy-app", Root, Long);
        // Webhooks (R1-09): creating one, or taking it over, lets an external caller fire restart / recreate / compose
        // up / Git deploy as root on its owner's authority. Over MCP only a Git-deploy webhook for an app is created.
        yield return Tool("webhook.save", "create_git_deploy_webhook", A, "webhook", Root, Short);
        // A new secret breaks every caller that still signs with the old one.
        yield return Tool("webhook.rotate_secret", "rotate_git_deploy_webhook_secret", A, "webhook", Dis, Short);
        yield return Ui("webhook.set_enabled", A, "webhook", Dis, Short);
        yield return Ui("webhook.delete", A, "webhook", Des, Short);

        // Networks
        yield return Tool("network.create", "create_network", A, "network", Rev, Short);
        yield return Tool("network.remove", "remove_network", A, "network", Des, Short);
        yield return Tool("network.connect", "connect_container_to_network", A, "network", Rev, Short);
        yield return Tool("network.disconnect", "disconnect_container_from_network", A, "network", Dis, Short);

        // Hosts
        // execute_command accepts up to 600 s of its own timeout; the deadline must not cut that short.
        yield return Tool("host.exec", "execute_command", A, "host", Root, TimeSpan.FromMinutes(11));
        // `docker system prune` from a scheduled cleanup task (run on schedule or with "Run now").
        yield return Ui("host.prune", W, "host", Dis, Short);
        yield return Tool("firewall.rule_add", "add_firewall_rule", A, "host", Dis, Short);
        yield return Tool("firewall.rule_remove", "remove_firewall_rule", A, "host", Dis, Short);
        yield return Ui("firewall.set_status", A, "host", Dis, Short);
        // INV-19: a site file with any content is loaded by the nginx master, which runs as root (a log or pid path
        // writes any file on the host), so the write is root-equivalent; it was Admin already.
        yield return Tool("nginx.update", "update_nginx_config", A, "host", Root, Short);
        yield return Ui("nginx.reload", A, "host", Dis, Short);
        yield return Ui("nginx.site_enable", A, "host", Dis, Short);
        yield return Ui("nginx.site_disable", A, "host", Dis, Short);
        yield return Tool("systemd.manage", "manage_systemd_service", A, "host", Dis, Normal);
        // R1-05: the page always required Admin (certbot runs as root on the host); the table takes the stricter level.
        yield return Tool("ssl.renew", "renew_ssl_certificate", A, "host", Rev, Normal);
        yield return Tool("certificate.renew_mtls", "renew_mtls_certificate", A, "host", Root, Normal);
        yield return Ui("certificate.issue_mtls", A, "host", Root, Normal);
        // R1-44 (contract 0007): per-host client credentials. Migration and renewal change which client a host's proxy
        // admits (every command to that host passes through it); each guarded host step may wait for its 3-minute
        // rollback, so the deadline is the long one. Retiring the shared key is UI-only and Admin.
        yield return Tool("mtls.client_migrate", "migrate_mtls_client_credential", A, "host", Root, Long);
        yield return Tool("mtls.client_renew", "renew_mtls_client_credential", A, "host", Root, Long);
        yield return Ui("mtls.shared_client_retire", A, "whiskers", Des, Short);

        // Databases
        yield return Tool("db.query", "execute_query", A, "container", Root, Normal);
        // R1-05: a dump holds every row of the database; the page always required Admin, the table takes it too.
        yield return Tool("db.backup", "backup_database", A, "container", Rev, Long);
        // Retention of a DB-backup task: deletes old dumps with a host `rm`.
        yield return Ui("db.backup_delete", A, "container", Des, Short);
        // Retention of a scheduled self-backup (the newest N are kept).
        yield return Ui("selfbackup.prune", A, "whiskers", Des, Short);

        // DNS
        yield return Tool("dns.record_set", "set_dns_record", W, "dns-zone", Dis, Short);
        yield return Tool("dns.record_delete", "delete_dns_record", W, "dns-zone", Des, Short);

        // Cloud servers
        // Servers page: onboarding installs the agent stack on the host as root.
        yield return Ui("server.onboard", A, "server", Root, Long);
        // Servers page: add / edit a server (host, credentials, SSH key, kubeconfig) or remove it.
        yield return Ui("server.save", A, "server", Root, Short);
        yield return Ui("server.remove", A, "server", Des, Short);
        // R1-57 (contract 0004): endpoint checks run on the host they are bound to. A run executes fixed probe commands
        // through the host executor as host.exec does, so it is RootEquivalent; the monitor runs it as the check's owner.
        yield return Ui("endpointcheck.save", A, "server", Rev, Short);
        yield return Ui("endpointcheck.delete", A, "server", Rev, Short);
        yield return Ui("endpointcheck.run", A, "server", Root, Normal);
        // AI triggers page: which events start the agent and what it is told to do.
        yield return Ui("aitrigger.save", A, "whiskers", Root, Short);
        // Settings: restore Whiskers' own data from a self-backup (maintenance, staged swap, process restart).
        yield return Ui("selfbackup.restore", A, "whiskers", Root, Normal);
        // Settings: create or delete one of Whiskers' own backup archives (a recovery point).
        yield return Ui("selfbackup.create", A, "whiskers", Rev, Long);
        yield return Ui("selfbackup.delete", A, "whiskers", Des, Short);
        yield return Tool("cloud.power_on", "cloud_power_on", A, "cloud-server", Rev, Normal);
        yield return Tool("cloud.shutdown", "cloud_shutdown", A, "cloud-server", Dis, Normal);
        yield return Tool("cloud.reboot", "cloud_reboot", A, "cloud-server", Dis, Normal);
        yield return Tool("cloud.hard_reset", "cloud_hard_reset", A, "cloud-server", Dis, Normal);
        yield return Tool("cloud.snapshot_create", "cloud_create_snapshot", A, "cloud-server", Rev, Long);
        yield return Tool("hetzner.rescue_enable", "hetzner_enable_rescue", A, "cloud-server", Root, Normal);
        yield return Tool("hetzner.rescue_disable", "hetzner_disable_rescue", A, "cloud-server", Rev, Normal);
        yield return Tool("hetzner.backups_enable", "hetzner_enable_backups", A, "cloud-server", Rev, Normal);
        yield return Tool("hetzner.backups_disable", "hetzner_disable_backups", A, "cloud-server", Des, Normal);
        yield return Tool("hetzner.server_type_change", "hetzner_change_server_type", A, "cloud-server", Dis, Long);
        yield return Tool("hetzner.snapshot_delete", "hetzner_delete_snapshot", A, "cloud-server", Des, Normal);

        // Automation and monitoring control (they cause or suppress future mutations)
        yield return Tool("scheduler.create", "create_scheduled_task", W, "scheduled-task", Root, Short);
        yield return Ui("scheduler.update", A, "scheduled-task", Root, Short);
        yield return Tool("scheduler.delete", "delete_scheduled_task", W, "scheduled-task", Rev, Short);
        yield return Tool("scheduler.run", "run_scheduled_task", W, "scheduled-task", Root, Long);
        yield return Tool("logalert.create", "create_log_alert", W, "log-alert", Rev, Short);
        yield return Ui("logalert.delete", W, "log-alert", Rev, Short);
        yield return Ui("logalert.toggle", W, "log-alert", Rev, Short);
        yield return Tool("checks.pause", "pause_server_checks", W, "server", Rev, Short);
        yield return Tool("checks.resume", "resume_server_checks", W, "server", Rev, Short);
        // INV-18 (user decision 2026-11-07): a manual CVE scan runs the scanners on every enabled host (apt index,
        // Trivy image and cache), and the in-app notification log is shared by everyone — both Operator. The cycle
        // scans one host after another, so it gets the fleet deadline. Risk: a scan changes caches only (package index,
        // scanner image, findings that the next scan replaces) and stops no workload, so it is Reversible, not
        // Disruptive; marking read loses no notification (the entries stay, only the unread badge goes), so it is
        // Reversible too; clearing deletes the history, so it is Destructive.
        yield return Ui("cve.scan", W, "fleet", Rev, TimeSpan.FromHours(4));
        yield return Ui("notifications.mark_read", W, "whiskers", Rev, Short);
        yield return Ui("notifications.clear", W, "whiskers", Des, Short);
        yield return Tool("autopatch.preview", "preview_auto_patch", W, "fleet", Rev, Long);
        // A cycle patches one container after another inside the nightly window; it must not be cut at 30 minutes.
        yield return Ui("autopatch.run", A, "fleet", Dis, TimeSpan.FromHours(4));
        yield return Ui("autoupdate.policy_set", A, "container", Dis, Short);
        // Who AutoPatch / AutoUpdate run as (PC-D6): an admin hands their authority to an unattended feature.
        yield return Ui("automation.owner_set", A, "fleet", Root, Short);
        // The in-app VPN bring-up at start (R1-43, S-07): starts the VPN daemon as root and joins the mesh with the
        // configured key. Unattended, on the authority of the admin who took it over; `tailscale up` waits up to 60 s.
        yield return Ui("vpn.bootstrap", A, "whiskers", Root, Normal);

        // Volume backups
        yield return Ui("volume.backup", W, "volume", Rev, Long);
        yield return Ui("volume.restore", A, "volume", Des, Long);
        yield return Ui("volume.backup_delete", A, "volume", Des, Short);

        // Security configuration (R1-11): who may sign in, with which role, which keys and tools the MCP surface
        // accepts, the guardrail policy, vault secrets and the settings sections. Each decides what every other
        // decision rests on, so all are Admin and root-equivalent; the id is the section, key or secret name.
        yield return Ui("config.save", A, "config", Root, Short);
        yield return Ui("config.whitelist", A, "config", Root, Short);
        yield return Ui("config.roles", A, "config", Root, Short);
        yield return Ui("config.mcp_permissions", A, "config", Root, Short);
        yield return Ui("config.agent", A, "config", Root, Short);
        yield return Ui("mcp.key_create", A, "config", Root, Short);
        yield return Ui("mcp.key_delete", A, "config", Dis, Short);
        yield return Ui("guardrails.save", A, "config", Root, Short);
        // The one configuration write the kill switch lets through: switching the kill switch itself off.
        yield return Ui("guardrails.kill_switch_off", A, "config", Root, Short);
        yield return Ui("vault.secret_set", A, "config", Root, Short);
        yield return Ui("vault.secret_delete", A, "config", Des, Short);
        yield return Ui("registry.save", A, "config", Root, Short);
        yield return Ui("registry.delete", A, "config", Dis, Short);

        // Local accounts (R1-49); the target id is the account's email. Adding an account, resetting another one's
        // password (which hands out a recovery code) and deleting one are Admin. Changing one's own password is the
        // one capability at read level: every signed-in user may do it for their own account, and the service refuses
        // anyone else's (it needs the current password too). The kill switch lets a read-level capability through.
        yield return Ui("account.password_change", McpPermissionLevels.Read, "account", Rev, Short);
        yield return Ui("account.create", A, "account", Root, Short);
        yield return Ui("account.password_reset", A, "account", Root, Short);
        yield return Ui("account.delete", A, "account", Des, Short);

        // Google/OIDC identity bindings (R1-27, contract 0003); the target id is the binding or pending-request id.
        // Confirming a request decides who signs in as a configured account: Admin and root-equivalent. Removing a
        // binding or request only takes a way to sign in away (the last Admin's last way is refused by the store).
        yield return Ui("identity.binding_confirm", A, "identity", Root, Short);
        yield return Ui("identity.binding_remove", A, "identity", Dis, Short);
    }
}
