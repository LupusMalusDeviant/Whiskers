using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Webhooks;

/// <summary>
/// What a webhook acts on, decided one way for the Webhooks page's gate and for the execution (R1-06): the page
/// decides on exactly the server and container the trigger then runs on.
/// </summary>
public static class WebhookTargets
{
    /// <summary>The server a webhook acts on. A webhook saved without one acts on the server with the id
    /// <c>local</c> — never on "whichever server is the default", which may be another machine.</summary>
    public static string ServerOf(WebhookEntity webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return string.IsNullOrWhiteSpace(webhook.ServerId) ? "local" : webhook.ServerId;
    }

    /// <summary>The container a restart / rebuild webhook acts on: exact id or name, else a unique id prefix, on
    /// <see cref="ServerOf"/>. An ambiguous prefix is an error, not the first match.</summary>
    public static async Task<(ContainerInfo? Container, string? Error)> ContainerAsync(IDockerService docker,
        WebhookEntity webhook, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(webhook);
        if (string.IsNullOrWhiteSpace(webhook.TargetId))
            return (null, "The webhook names no container.");
        var containers = await docker.ListContainersAsync(all: true, serverId: ServerOf(webhook), ct);
        return McpInputValidation.Resolve(containers, webhook.TargetId);
    }

    /// <summary>The plan of a compose deploy webhook (R1-07), the same for the page's Test and the trigger: the
    /// compose project directory (<c>TargetId</c>) must be an absolute path — it reaches the host shell (quoted).</summary>
    public static async Task<(ComposePlan? Plan, string? Error)> ComposePlanAsync(IComposePlanner planner,
        WebhookEntity webhook, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(webhook);
        var dir = webhook.TargetId ?? "";
        if (!dir.StartsWith('/'))
            return (null, "Deploy target must be an absolute path.");
        try
        {
            return (await planner.PlanAsync(ComposeDeployment.FromDirectory(ServerOf(webhook), dir), ct), null);
        }
        catch (ComposePlanException ex)
        {
            return (null, ex.Message);
        }
    }
}
