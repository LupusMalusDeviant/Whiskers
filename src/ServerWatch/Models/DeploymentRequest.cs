namespace ServerWatch.Models;

public class DeploymentRequest
{
    public string Image { get; set; } = string.Empty;
    public string? ContainerName { get; set; }
    public Dictionary<string, string> PortMappings { get; set; } = new();
    /// <summary>Optional bind IP per host port (from `ip:host:container` compose syntax): host port → bind IP.
    /// Absent = bind all interfaces (Docker default).</summary>
    public Dictionary<string, string> PortBindIps { get; set; } = new();
    public List<string> Volumes { get; set; } = new();
    public Dictionary<string, string> EnvironmentVars { get; set; } = new();
    public string RestartPolicy { get; set; } = "unless-stopped";
    public string? NetworkName { get; set; }
}

public class ComposeDeployRequest
{
    public string YamlContent { get; set; } = string.Empty;
}
