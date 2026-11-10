using System.Text.Json;
using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D4 b): a <see cref="MutationTargetAttribute"/> argument is bound to the object the decision resolved —
/// its id, name or aliases on the decided server. What the object also touches (<see cref="MutationTarget.Related"/>)
/// counts only for a call composed into the decision or a decision that composes or plans; an argument value policy
/// merely saw never authorizes a target.
/// </summary>
public sealed class MutationTargetBindingTests
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private static MutationScope Decide(string capability, MutationTarget target, object? arguments = null)
    {
        var args = JsonSerializer.SerializeToElement(arguments ?? new { })
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        return MutationScope.Begin(capability, "op-" + Guid.NewGuid().ToString("N"), Admin,
            new MutationRequest(capability, Admin, MutationSurface.Ui, target, args));
    }

    private static bool Authorizes(string capability, string identity, string? server = "local") =>
        MutationScope.AuthorizesTarget(capability, identity, actsOnServer: server is not null, server, server, id => id);

    [Fact]
    public void The_decided_id_name_and_aliases_authorize_the_target()
    {
        using var scope = Decide("logalert.delete", new MutationTarget("log-alert", "rule-1", "Disk full")
            { Aliases = ["disk-full"] });

        Assert.True(Authorizes("logalert.delete", "rule-1", server: null));
        Assert.True(Authorizes("logalert.delete", "Disk full", server: null));
        Assert.True(Authorizes("logalert.delete", "disk-full", server: null));
    }

    [Fact]
    public void An_argument_value_policy_saw_does_not_authorize_a_target()
    {
        using var scope = Decide("container.restart", new MutationTarget("container", "c-1", "web", "local"),
            new { containerId = "web", note = "c-2" });

        Assert.True(Authorizes("container.restart", "c-1"));
        Assert.False(Authorizes("container.restart", "c-2"));
    }

    [Fact]
    public void A_related_resource_authorizes_only_a_composing_decision()
    {
        var target = new MutationTarget("container", "c-1", "web", "local") { Related = ["db"] };
        using (Decide("container.restart", target))
            Assert.False(Authorizes("container.restart", "db"));

        // compose.deploy plans its containers: the shared ones it decided are related, and a composed stop may name one.
        using (Decide("compose.deploy", new MutationTarget("server", "local", ServerId: "local") { Related = ["db"] }))
            Assert.True(Authorizes("container.stop", "db"));
    }

    [Fact]
    public void The_decided_object_on_another_server_is_not_authorized()
    {
        using var scope = Decide("container.restart", new MutationTarget("container", "c-1", "web", "local"));

        Assert.False(Authorizes("container.restart", "c-1", server: "other"));
    }
}
