using ServerWatch.Models;
using ServerWatch.Services.Docker;

namespace ServerWatch.Services.Deployment;

public class DeploymentService : IDeploymentService
{
    private readonly IDockerService _docker;
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(IDockerService docker, ILogger<DeploymentService> logger)
    {
        _docker = docker;
        _logger = logger;
    }

    public async Task<string> DeployFromFormAsync(DeploymentRequest request, IProgress<string>? progress = null)
    {
        progress?.Report($"Pulling image {request.Image}...");
        await _docker.PullImageAsync(request.Image, progress);

        progress?.Report("Creating container...");
        var containerId = await _docker.CreateContainerAsync(request);

        progress?.Report($"Container {containerId[..12]} started successfully");
        _logger.LogInformation("Deployed container {ContainerId} from image {Image}",
            containerId[..12], request.Image);

        return containerId;
    }

    public async Task<IList<string>> DeployFromComposeAsync(string yamlContent, IProgress<string>? progress = null)
    {
        var validation = ValidateCompose(yamlContent);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Compose validation failed: {string.Join("; ", validation.Errors)}");
        }

        var containerIds = new List<string>();
        foreach (var service in validation.Services)
        {
            progress?.Report($"Deploying service: {service.ContainerName ?? service.Image}");
            var id = await DeployFromFormAsync(service, progress);
            containerIds.Add(id);
        }

        return containerIds;
    }

    public DeploymentValidationResult ValidateCompose(string yamlContent)
    {
        return ComposeFileParser.Parse(yamlContent);
    }
}
