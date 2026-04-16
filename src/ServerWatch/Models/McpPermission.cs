namespace ServerWatch.Models;

public class McpPermissionData
{
    public List<McpApiKeyConfig> ApiKeys { get; set; } = new();
    public Dictionary<string, McpToolConfig> Tools { get; set; } = new();
}

public class McpApiKeyConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
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

    public static readonly Dictionary<string, string> DefaultToolLevels = new()
    {
        // Read tools
        ["list_containers"] = Read,
        ["get_container_details"] = Read,
        ["get_container_logs"] = Read,
        ["list_servers"] = Read,
        ["get_server_info"] = Read,
        ["get_server_logs"] = Read,
        ["get_health_summary"] = Read,
        ["get_container_metrics"] = Read,
        ["get_server_metrics"] = Read,
        ["get_update_status"] = Read,
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
        ["deploy_app"] = Write,
        ["deploy_compose"] = Write,
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

        // Scheduler tools
        ["list_scheduled_tasks"] = Read,
        ["create_scheduled_task"] = Write,
        ["delete_scheduled_task"] = Write,
        ["run_scheduled_task"] = Write,

        // Network tools
        ["list_networks"] = Read,
        ["create_network"] = Write,
        ["remove_network"] = Write,
        ["connect_network"] = Write,
        ["disconnect_network"] = Write,

        // Resource limit tools
        ["update_container_resources"] = Write,

        // Admin tools
        ["execute_command"] = Admin,

        // Coolify read tools
        ["list_coolify_applications"] = Read,
        ["get_coolify_application"] = Read,
        ["get_coolify_application_logs"] = Read,
        ["list_coolify_servers"] = Read,
        ["list_coolify_databases"] = Read,
        ["get_coolify_env_vars"] = Read,

        // Coolify write tools
        ["deploy_coolify_application"] = Write,
        ["start_coolify_application"] = Write,
        ["stop_coolify_application"] = Write,
        ["restart_coolify_application"] = Write,
        ["deploy_coolify_by_tag"] = Write,
        ["set_coolify_env_var"] = Write,
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
        ["update_container_resources"] = "Container",
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
        ["connect_network"] = "Netzwerk",
        ["disconnect_network"] = "Netzwerk",
        ["deploy_app"] = "Deployment",
        ["deploy_compose"] = "Deployment",
        ["get_update_status"] = "Monitoring",
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

        // Coolify tools
        ["list_coolify_applications"] = "Coolify",
        ["get_coolify_application"] = "Coolify",
        ["get_coolify_application_logs"] = "Coolify",
        ["list_coolify_servers"] = "Coolify",
        ["list_coolify_databases"] = "Coolify",
        ["get_coolify_env_vars"] = "Coolify",
        ["deploy_coolify_application"] = "Coolify",
        ["start_coolify_application"] = "Coolify",
        ["stop_coolify_application"] = "Coolify",
        ["restart_coolify_application"] = "Coolify",
        ["deploy_coolify_by_tag"] = "Coolify",
        ["set_coolify_env_var"] = "Coolify",
    };
}
