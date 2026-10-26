using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Deployment;

public interface IDeploymentService
{
    [MutatingOperation("app.deploy")]
    Task<string> DeployFromFormAsync(DeploymentRequest request, IProgress<string>? progress = null, string? serverId = null);
    [MutatingOperation("compose.deploy")]
    Task<IList<string>> DeployFromComposeAsync(string yamlContent, IProgress<string>? progress = null, string? serverId = null);
    DeploymentValidationResult ValidateCompose(string yamlContent);
}

public class DeploymentValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<DeploymentRequest> Services { get; set; } = new();
}
