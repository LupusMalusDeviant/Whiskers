using System.Text.Json.Serialization;

namespace ServerWatch.Models.Coolify;

public class CoolifyApplication
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("fqdn")]
    public string? Fqdn { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("git_repository")]
    public string? GitRepository { get; set; }

    [JsonPropertyName("git_branch")]
    public string? GitBranch { get; set; }

    [JsonPropertyName("build_pack")]
    public string? BuildPack { get; set; }

    [JsonPropertyName("docker_compose_location")]
    public string? DockerComposeLocation { get; set; }

    [JsonPropertyName("dockerfile_location")]
    public string? DockerfileLocation { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("health_check_enabled")]
    public bool? HealthCheckEnabled { get; set; }

    [JsonPropertyName("health_check_path")]
    public string? HealthCheckPath { get; set; }

    /// <summary>
    /// Derived display status for UI badges.
    /// </summary>
    public string DisplayStatus => Status?.ToLowerInvariant() switch
    {
        "running" or "running:healthy" => "running",
        "stopped" or "exited" => "stopped",
        var s when s?.Contains("deploying") == true => "deploying",
        _ => Status ?? "unknown"
    };
}
