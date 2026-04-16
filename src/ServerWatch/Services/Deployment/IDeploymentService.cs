using ServerWatch.Models;

namespace ServerWatch.Services.Deployment;

public interface IDeploymentService
{
    Task<string> DeployFromFormAsync(DeploymentRequest request, IProgress<string>? progress = null);
    Task<IList<string>> DeployFromComposeAsync(string yamlContent, IProgress<string>? progress = null);
    DeploymentValidationResult ValidateCompose(string yamlContent);
}

public class DeploymentValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<DeploymentRequest> Services { get; set; } = new();
}
