using Whiskers.Models;

namespace Whiskers.Services.Deployment;

/// <summary>The Core's default <see cref="IDeploymentService"/> for when the Deployment module is off.
/// <c>ContainerTools</c> — an MCP class that mixes core container ops (list/restart/logs/…) with
/// <c>deploy_app</c>/<c>deploy_compose</c> — can't be split under the byte-gleich rule, so it stays in Core and
/// injects this per call. The no-op keeps that resolution working: the deploy operations <b>throw</b> (never
/// fake a deploy — a bogus "deployed: …" would be worse than a clear failure), and <c>ValidateCompose</c>
/// reports invalid. The real <see cref="DeploymentService"/> wins by last-registration when enabled.
/// Registered scoped, matching the real service. Soft-dependency-via-no-op-Core-contract (RoadToSAP §2.1).</summary>
public sealed class NoopDeploymentService : IDeploymentService
{
    private const string Disabled =
        "The Deployment module is disabled (set Features:deployment:Enabled=true to enable deployments).";

    public Task<string> DeployFromFormAsync(DeploymentRequest request, IProgress<string>? progress = null, string? serverId = null)
        => throw new InvalidOperationException(Disabled);

    public Task<IList<string>> DeployFromComposeAsync(string yamlContent, IProgress<string>? progress = null, string? serverId = null)
        => throw new InvalidOperationException(Disabled);

    public DeploymentValidationResult ValidateCompose(string yamlContent)
        => new() { IsValid = false, Errors = { Disabled } };
}
