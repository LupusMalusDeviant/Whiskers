using System.Text.Json.Serialization;

namespace ServerWatch.Models.Coolify;

public class CoolifyDeployment
{
    [JsonPropertyName("deployment_uuid")]
    public string? DeploymentUuid { get; set; }

    [JsonPropertyName("resource_uuid")]
    public string? ResourceUuid { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class CoolifyDeployResponse
{
    [JsonPropertyName("deployments")]
    public List<CoolifyDeployment> Deployments { get; set; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
