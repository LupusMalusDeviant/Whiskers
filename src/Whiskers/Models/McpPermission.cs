namespace Whiskers.Models;

public class McpPermissionData
{
    public List<McpApiKeyConfig> ApiKeys { get; set; } = new();
    public Dictionary<string, McpToolConfig> Tools { get; set; } = new();
}

public class McpApiKeyConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>The plaintext key. Set ONLY on the instance <c>AddApiKeyAsync</c> returns (shown to the creator once)
    /// and when an old file is read for migration; never persisted — <c>null</c> is skipped on write.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }
    /// <summary>Lower-case hex SHA-256 of the key — what is stored and compared. A fast hash is enough here: keys
    /// are 256-bit random values, so there is nothing for a slow KDF to protect against brute force.</summary>
    public string KeyHash { get; set; } = "";
    /// <summary>The key's last four characters, so the UI can tell keys apart without holding the secret.</summary>
    public string KeyHint { get; set; } = "";
    public string PermissionLevel { get; set; } = "read"; // read, write, admin
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string>? AllowedTools { get; set; } // null = use PermissionLevel defaults
}

public class McpToolConfig
{
    public bool Enabled { get; set; } = true;
    public string RequiredLevel { get; set; } = "read"; // minimum permission level
    public string Category { get; set; } = "read"; // read, write, admin
}

public static class McpPermissionLevels
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Admin = "admin";

    public static int GetRank(string level) => level switch
    {
        Read => 1,
        Write => 2,
        Admin => 3,
        _ => 0
    };

    public static bool HasAccess(string keyLevel, string requiredLevel)
        => GetRank(keyLevel) >= GetRank(requiredLevel);

    /// <summary>Coerces arbitrary (config-supplied) level text to a known level. Unknown/empty values
    /// fail safe to the least privilege (read), so a malformed ceiling can never widen access.</summary>
    public static string Normalize(string? level) => (level?.Trim().ToLowerInvariant()) switch
    {
        Read => Read,
        Write => Write,
        Admin => Admin,
        _ => Read
    };

    /// <summary>The level each tool requires, as the permission check reads it. An unlisted tool resolves to
    /// <see cref="Admin"/> (fail-closed) — which also means a forgotten entry produces a tool that is registered
    /// and listed but denied to the agent on every call, silently.
    ///
    /// <para>Do not edit this map on its own. The level is <b>declared</b> on the tool method via
    /// <c>[McpToolLevel]</c>; this dictionary must mirror those declarations exactly. <c>McpToolLevelTests</c>
    /// compares the two in both directions and fails the build on any drift — including an entry here for a tool
    /// that no longer exists. Change the attribute, then bring this entry along.</para></summary>
    public static readonly Dictionary<string, string> DefaultToolLevels = new()
    {
        // Read tools
        ["list_containers"] = Read,
        // GAP-6: reads image metadata and pulls the candidate image; starts and restarts nothing.
        ["assess_update_risk"] = Read,
        // GAP-7: reads compose labels only.
        ["get_blast_radius"] = Read,
        ["get_container_details"] = Read,
        ["get_container_logs"] = Read,
        ["list_servers"] = Read,
        ["get_server_info"] = Read,
        ["get_server_logs"] = Read,
        ["get_health_summary"] = Read,
        ["get_container_metrics"] = Read,
        ["get_server_metrics"] = Read,
        ["get_update_status"] = Read,
        // Reads the last nightly auto-patch report; touches nothing.
        ["get_auto_patch_status"] = Read,
        ["get_cve_summary"] = Read,
        ["get_server_cves"] = Read,
        ["get_container_cves"] = Read,
        ["list_cve_groups"] = Read,
        ["list_firewall_rules"] = Read,
        ["list_nginx_sites"] = Read,
        ["get_nginx_config"] = Read,
        ["list_systemd_services"] = Read,
        ["list_ssl_certificates"] = Read,
        ["get_container_env"] = Read,

        // Write tools
        ["set_container_env"] = Write,
        ["start_container"] = Write,
        ["stop_container"] = Write,
        ["restart_container"] = Write,
        ["update_container"] = Write,
        // Dry run only: pulls candidate images and puts the old tags back, recreates nothing.
        ["preview_auto_patch"] = Write,
        // Admin (not Write): both accept arbitrary host bind-mounts / images, so a container they create
        // can mount the host root = de-facto root on the host. Keep them above the write boundary.
        ["deploy_app"] = Admin,
        ["deploy_compose"] = Admin,
        ["renew_mtls_certificate"] = Admin,  // swaps the key of a proxy every command to that host passes through
        ["add_firewall_rule"] = Write,
        ["remove_firewall_rule"] = Write,
        ["update_nginx_config"] = Write,
        ["manage_systemd_service"] = Write,
        ["renew_ssl_certificate"] = Write,

        // Database tools
        ["detect_database"] = Read,
        ["list_databases"] = Read,
        ["list_tables"] = Read,
        ["get_schema"] = Read,
        ["execute_query"] = Write,
        ["backup_database"] = Write,

        // Log tools
        ["search_logs"] = Read,
        ["list_log_alerts"] = Read,
        ["create_log_alert"] = Write,

        // Git deploy / volume backups / alert history — read-only for now (Plan-0013 WP4). Triggering a
        // deploy, taking or restoring a backup, and sending notifications are deliberately NOT exposed.
        ["list_git_deploy_apps"] = Read,
        ["list_volume_backups"] = Read,
        ["list_volumes"] = Read,
        ["list_recent_alerts"] = Read,
        // Plan-0005 WP2 — the emergency stop. Pausing is Write, not Admin: it makes Whiskers do LESS,
        // and putting it behind the highest bar would mean the load-shedding switch is the one thing an
        // operator-level key cannot reach while the fleet is under load.
        ["get_log_hygiene_report"] = Read,   // Plan-0007 WP-MCP — no write counterpart, by design
        ["get_action_outcomes"] = Read,       // Plan-0006 WP-MCP — no counterpart that clears the record
        ["get_host_load"] = Read,             // Plan-0004 WP-MCP — no threshold-setting counterpart, by design
        ["get_whiskers_self_status"] = Read,   // Plan-0003 WP-MCP — no write counterpart: an agent that
        // could reset these counters could erase the evidence that something has been broken for a week
        ["list_paused_servers"] = Read,
        ["pause_server_checks"] = Write,
        ["resume_server_checks"] = Write,

        // Scheduler tools
        ["list_scheduled_tasks"] = Read,
        ["create_scheduled_task"] = Write,
        ["delete_scheduled_task"] = Write,
        ["run_scheduled_task"] = Write,

        // Network tools
        ["list_networks"] = Read,
        ["create_network"] = Write,
        ["remove_network"] = Write,
        ["connect_container_to_network"] = Write,
        ["disconnect_container_from_network"] = Write,

        // Admin tools
        ["execute_command"] = Admin,

        // Agent: an MCP caller (e.g. external Claude Code) instructs the in-process agent.
        // Read level, because the agent only inherits the caller's rights anyway. NOT agent-callable
        // (see AgentToolRegistry.NonAgentTools) — otherwise the agent would call itself recursively.
        ["instruct_agent"] = Read,

        // Cloud (provider-agnostic) read tools
        ["list_cloud_servers"] = Read,
        ["cloud_status"] = Read,
        ["cloud_metrics"] = Read,

        // Cloud (provider-agnostic) write tools
        ["cloud_power_on"] = Write,
        ["cloud_shutdown"] = Write,
        ["cloud_reboot"] = Write,
        ["cloud_hard_reset"] = Write,
        ["cloud_create_snapshot"] = Write,

        // Hetzner-only extras
        ["hetzner_list_snapshots"] = Read,
        ["hetzner_enable_rescue"] = Admin,   // issues a root password for the server
        ["hetzner_disable_rescue"] = Write,
        ["hetzner_enable_backups"] = Write,
        ["hetzner_disable_backups"] = Write,
        ["hetzner_change_server_type"] = Write,
        ["hetzner_delete_snapshot"] = Write,

        // DNS (Modules/Dns): one global provider token, zone-fenced. delete is Write, not Admin — it is the
        // undo of set_dns_record and limited to the same four record types (never NS/SOA/MX).
        ["list_dns_records"] = Read,
        ["set_dns_record"] = Write,
        ["delete_dns_record"] = Write,
    };

    public static readonly Dictionary<string, string> ToolCategories = new()
    {
        ["list_containers"] = "Container",
        ["get_container_details"] = "Container",
        ["get_container_logs"] = "Container",
        ["start_container"] = "Container",
        ["stop_container"] = "Container",
        ["restart_container"] = "Container",
        ["update_container"] = "Container",
        ["get_container_env"] = "Container",
        ["set_container_env"] = "Container",
        ["search_logs"] = "Logs",
        ["list_log_alerts"] = "Logs",
        ["create_log_alert"] = "Logs",
        ["list_scheduled_tasks"] = "Scheduler",
        ["create_scheduled_task"] = "Scheduler",
        ["delete_scheduled_task"] = "Scheduler",
        ["run_scheduled_task"] = "Scheduler",
        ["detect_database"] = "Datenbank",
        ["list_databases"] = "Datenbank",
        ["list_tables"] = "Datenbank",
        ["get_schema"] = "Datenbank",
        ["execute_query"] = "Datenbank",
        ["backup_database"] = "Datenbank",
        ["list_networks"] = "Netzwerk",
        ["create_network"] = "Netzwerk",
        ["remove_network"] = "Netzwerk",
        ["connect_container_to_network"] = "Netzwerk",
        ["disconnect_container_from_network"] = "Netzwerk",
        ["deploy_app"] = "Deployment",
        ["deploy_compose"] = "Deployment",
        ["renew_mtls_certificate"] = "Server",
        ["get_update_status"] = "Monitoring",
        ["get_auto_patch_status"] = "Monitoring",
        ["preview_auto_patch"] = "Container",
        ["get_cve_summary"] = "Monitoring",
        ["get_server_cves"] = "Monitoring",
        ["get_container_cves"] = "Monitoring",
        ["list_cve_groups"] = "Monitoring",
        ["get_health_summary"] = "Monitoring",
        ["get_container_metrics"] = "Monitoring",
        ["get_server_metrics"] = "Monitoring",
        ["get_server_logs"] = "Monitoring",
        ["list_servers"] = "Server",
        ["get_server_info"] = "Server",
        ["list_firewall_rules"] = "Firewall",
        ["add_firewall_rule"] = "Firewall",
        ["remove_firewall_rule"] = "Firewall",
        ["list_nginx_sites"] = "Nginx",
        ["get_nginx_config"] = "Nginx",
        ["update_nginx_config"] = "Nginx",
        ["list_systemd_services"] = "Systemd",
        ["manage_systemd_service"] = "Systemd",
        ["list_ssl_certificates"] = "SSL",
        ["renew_ssl_certificate"] = "SSL",
        ["execute_command"] = "Admin",
        ["instruct_agent"] = "Agent",
        ["list_git_deploy_apps"] = "Git Deploy",
        ["list_volume_backups"] = "Volume-Backups",
        ["list_volumes"] = "Volume-Backups",
        ["list_recent_alerts"] = "Benachrichtigungen",
        ["get_log_hygiene_report"] = "Logs",
        ["get_action_outcomes"] = "Überwachung",
        ["get_host_load"] = "Überwachung",
        ["get_whiskers_self_status"] = "Überwachung",
        ["list_paused_servers"] = "Überwachung",
        ["pause_server_checks"] = "Überwachung",
        ["resume_server_checks"] = "Überwachung",

        // Cloud (Hetzner/Hostinger)
        ["list_cloud_servers"] = "Cloud",
        ["cloud_status"] = "Cloud",
        ["cloud_metrics"] = "Cloud",
        ["cloud_power_on"] = "Cloud",
        ["cloud_shutdown"] = "Cloud",
        ["cloud_reboot"] = "Cloud",
        ["cloud_hard_reset"] = "Cloud",
        ["cloud_create_snapshot"] = "Cloud",
        ["hetzner_list_snapshots"] = "Cloud",
        ["hetzner_enable_rescue"] = "Cloud",
        ["hetzner_disable_rescue"] = "Cloud",
        ["hetzner_enable_backups"] = "Cloud",
        ["hetzner_disable_backups"] = "Cloud",
        ["hetzner_change_server_type"] = "Cloud",
        ["hetzner_delete_snapshot"] = "Cloud",

        // DNS
        ["list_dns_records"] = "DNS",
        ["set_dns_record"] = "DNS",
        ["delete_dns_record"] = "DNS",
    };
}
