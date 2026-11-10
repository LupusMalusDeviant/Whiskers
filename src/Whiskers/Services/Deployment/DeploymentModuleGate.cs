using Whiskers.Modules;

namespace Whiskers.Services.Deployment;

/// <summary>
/// An <c>app.deploy</c> (MCP <c>deploy_app</c>, the agent, the Deploy and App Store pages) needs the Deployment module:
/// with the module off it is refused before it is planned, with a reason that names the switch. <c>deploy_app</c> lives
/// in the Core-resident <c>ContainerTools</c>, so it checks this itself; the target resolver checks it for every surface.
/// </summary>
public static class DeploymentModuleGate
{
    /// <summary>The module id (<see cref="Whiskers.Modules.Deployment.DeploymentModule.Id"/>).</summary>
    public const string ModuleId = "deployment";

    /// <summary>Why an app deployment is refused while the module is off.</summary>
    public const string DisabledReason =
        "the Deployment module is disabled (set Features:deployment:Enabled=true to enable deployments).";

    /// <summary>True when a module registry is present and says the Deployment module is off. Without a registry (a
    /// host without the module system, as in unit tests) nothing is gated here.</summary>
    public static bool IsOff(IModuleRegistry? modules) => modules is not null && !modules.IsEnabled(ModuleId);
}
