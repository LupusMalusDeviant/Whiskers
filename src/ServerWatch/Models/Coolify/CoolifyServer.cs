using System.Text.Json.Serialization;

namespace ServerWatch.Models.Coolify;

public class CoolifyServer
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("is_reachable")]
    public bool IsReachable { get; set; }

    [JsonPropertyName("is_usable")]
    public bool IsUsable { get; set; }

    [JsonPropertyName("settings")]
    public CoolifyServerSettings? Settings { get; set; }
}

public class CoolifyServerSettings
{
    [JsonPropertyName("docker_version")]
    public string? DockerVersion { get; set; }

    [JsonPropertyName("is_swarm_manager")]
    public bool? IsSwarmManager { get; set; }
}
