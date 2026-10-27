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
/// WP2 step 2.0: the catalog reproduces today's levels and changes no behaviour. A capability exposed as an MCP
/// tool takes the tool's level from <see cref="McpPermissionLevels.DefaultToolLevels"/>, so the two cannot drift
/// while both exist. A capability without a tool carries the role its UI handler checks today. Later steps make
/// this table the source and retire <c>DefaultToolLevels</c>.
/// </summary>
public sealed class CapabilityCatalog : ICapabilityCatalog
{
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

    private static Capability Tool(string id, string tool, string target, CapabilityRisk risk, TimeSpan deadline) =>
        new(id, McpPermissionLevels.DefaultToolLevels[tool], target, risk, tool, deadline);

    private static Capability Ui(string id, string level, string target, CapabilityRisk risk, TimeSpan deadline) =>
        new(id, level, target, risk, null, deadline);

    private static IEnumerable<Capability> Definitions()
    {
        const string W = McpPermissionLevels.Write, A = McpPermissionLevels.Admin;
        const CapabilityRisk Rev = CapabilityRisk.Reversible, Dis = CapabilityRisk.Disruptive,
            Des = CapabilityRisk.Destructive, Root = CapabilityRisk.RootEquivalent;

        // Containers and images
        yield return Tool("container.start", "start_container", "container", Rev, Normal);
        yield return Tool("container.stop", "stop_container", "container", Dis, Normal);
        yield return Tool("container.restart", "restart_container", "container", Dis, Normal);
        yield return Tool("container.update", "update_container", "container", Dis, Long);
        yield return Tool("container.env_set", "set_container_env", "container", Dis, Normal);
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
        yield return Ui("image.pull", W, "host", Rev, Long);

        // Deployments
        yield return Tool("app.deploy", "deploy_app", "host", Root, Long);
        yield return Tool("compose.deploy", "deploy_compose", "host", Root, Long);
        yield return Ui("gitdeploy.save", A, "git-deploy-app", Root, Short);
        yield return Ui("gitdeploy.delete", A, "git-deploy-app", Rev, Short);
        yield return Ui("gitdeploy.deploy", W, "git-deploy-app", Root, Long);

        // Networks
        yield return Tool("network.create", "create_network", "network", Rev, Short);
        yield return Tool("network.remove", "remove_network", "network", Des, Short);
        yield return Tool("network.connect", "connect_container_to_network", "network", Rev, Short);
        yield return Tool("network.disconnect", "disconnect_container_from_network", "network", Dis, Short);

        // Hosts
        // execute_command accepts up to 600 s of its own timeout; the deadline must not cut that short.
        yield return Tool("host.exec", "execute_command", "host", Root, TimeSpan.FromMinutes(11));
        // `docker system prune` from a scheduled cleanup task (run on schedule or with "Run now").
        yield return Ui("host.prune", W, "host", Dis, Short);
        yield return Tool("firewall.rule_add", "add_firewall_rule", "host", Dis, Short);
        yield return Tool("firewall.rule_remove", "remove_firewall_rule", "host", Dis, Short);
        yield return Ui("firewall.set_status", A, "host", Dis, Short);
        yield return Tool("nginx.update", "update_nginx_config", "host", Dis, Short);
        yield return Ui("nginx.reload", A, "host", Dis, Short);
        yield return Ui("nginx.site_enable", A, "host", Dis, Short);
        yield return Ui("nginx.site_disable", A, "host", Dis, Short);
        yield return Tool("systemd.manage", "manage_systemd_service", "host", Dis, Normal);
        yield return Tool("ssl.renew", "renew_ssl_certificate", "host", Rev, Normal);
        yield return Tool("certificate.renew_mtls", "renew_mtls_certificate", "host", Root, Normal);
        yield return Ui("certificate.issue_mtls", A, "host", Root, Normal);

        // Databases
        yield return Tool("db.query", "execute_query", "container", Root, Normal);
        yield return Tool("db.backup", "backup_database", "container", Rev, Long);
        // Retention of a DB-backup task: deletes old dumps with a host `rm`.
        yield return Ui("db.backup_delete", A, "container", Des, Short);

        // DNS
        yield return Tool("dns.record_set", "set_dns_record", "dns-zone", Dis, Short);
        yield return Tool("dns.record_delete", "delete_dns_record", "dns-zone", Des, Short);

        // Cloud servers
        // Servers page: onboarding installs the agent stack on the host as root.
        yield return Ui("server.onboard", A, "server", Root, Long);
        // Servers page: add / edit a server (host, credentials, SSH key, kubeconfig) or remove it.
        yield return Ui("server.save", A, "server", Root, Short);
        yield return Ui("server.remove", A, "server", Des, Short);
        // AI triggers page: which events start the agent and what it is told to do.
        yield return Ui("aitrigger.save", A, "whiskers", Root, Short);
        // Settings: restore Whiskers' own data from a self-backup (maintenance, staged swap, process restart).
        yield return Ui("selfbackup.restore", A, "whiskers", Root, Normal);
        // Settings: create or delete one of Whiskers' own backup archives (a recovery point).
        yield return Ui("selfbackup.create", A, "whiskers", Rev, Long);
        yield return Ui("selfbackup.delete", A, "whiskers", Des, Short);
        yield return Tool("cloud.power_on", "cloud_power_on", "cloud-server", Rev, Normal);
        yield return Tool("cloud.shutdown", "cloud_shutdown", "cloud-server", Dis, Normal);
        yield return Tool("cloud.reboot", "cloud_reboot", "cloud-server", Dis, Normal);
        yield return Tool("cloud.hard_reset", "cloud_hard_reset", "cloud-server", Dis, Normal);
        yield return Tool("cloud.snapshot_create", "cloud_create_snapshot", "cloud-server", Rev, Long);
        yield return Tool("hetzner.rescue_enable", "hetzner_enable_rescue", "cloud-server", Root, Normal);
        yield return Tool("hetzner.rescue_disable", "hetzner_disable_rescue", "cloud-server", Rev, Normal);
        yield return Tool("hetzner.backups_enable", "hetzner_enable_backups", "cloud-server", Rev, Normal);
        yield return Tool("hetzner.backups_disable", "hetzner_disable_backups", "cloud-server", Des, Normal);
        yield return Tool("hetzner.server_type_change", "hetzner_change_server_type", "cloud-server", Dis, Long);
        yield return Tool("hetzner.snapshot_delete", "hetzner_delete_snapshot", "cloud-server", Des, Normal);

        // Automation and monitoring control (they cause or suppress future mutations)
        yield return Tool("scheduler.create", "create_scheduled_task", "scheduled-task", Root, Short);
        yield return Ui("scheduler.update", A, "scheduled-task", Root, Short);
        yield return Tool("scheduler.delete", "delete_scheduled_task", "scheduled-task", Rev, Short);
        yield return Tool("scheduler.run", "run_scheduled_task", "scheduled-task", Root, Long);
        yield return Tool("logalert.create", "create_log_alert", "log-alert", Rev, Short);
        yield return Ui("logalert.delete", W, "log-alert", Rev, Short);
        yield return Ui("logalert.toggle", W, "log-alert", Rev, Short);
        yield return Tool("checks.pause", "pause_server_checks", "server", Rev, Short);
        yield return Tool("checks.resume", "resume_server_checks", "server", Rev, Short);
        yield return Tool("autopatch.preview", "preview_auto_patch", "fleet", Rev, Long);
        yield return Ui("autopatch.run", A, "fleet", Dis, Long);
        yield return Ui("autoupdate.policy_set", A, "container", Dis, Short);

        // Volume backups
        yield return Ui("volume.backup", W, "volume", Rev, Long);
        yield return Ui("volume.restore", A, "volume", Des, Long);
        yield return Ui("volume.backup_delete", A, "volume", Des, Short);
    }
}
