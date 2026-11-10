using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (INV-01): a principal built by its production factory carries every field the scheme table requires
/// for its kind and none it forbids; <see cref="PrincipalIds.KindOf"/> maps its id back to its kind; and no id is
/// derived from a display name, so renaming keeps the identity.
/// </summary>
public sealed partial class PrincipalCompletenessTests
{
    private static AgentPrincipal Key(string keyId, string name) =>
        new(AgentPrincipalKind.McpKey, name, McpPermissionLevels.Write, null, McpKeyId: keyId);

    [Fact]
    public void An_api_key_is_keyed_by_its_id_and_names_it_as_credential()
    {
        var p = MutationPrincipals.Of(Key("k1", "CI key"));

        Assert.Equal(MutationPrincipalKind.ApiKey, p.Kind);
        Assert.Equal("key:k1", p.Id);
        Assert.Equal("k1", p.CredentialId);
        Assert.Null(p.OnBehalfOf);
        Assert.Equal(MutationPrincipalKind.ApiKey, PrincipalIds.KindOf(p.Id));
        // Renaming the key keeps the identity.
        Assert.Equal(p.Id, MutationPrincipals.Of(Key("k1", "renamed")).Id);
    }

    [Fact]
    public void An_agent_is_its_session_acting_for_its_source()
    {
        var p = MutationPrincipals.Agent(Key("k1", "CI key"), "sess-9");

        Assert.Equal(MutationPrincipalKind.Agent, p.Kind);
        Assert.Equal("agent:sess-9", p.Id);
        Assert.Equal("sess-9", p.SessionId);
        Assert.Equal("k1", p.CredentialId);
        Assert.Equal("key:k1", p.OnBehalfOf!.Id);
        Assert.Equal(MutationPrincipalKind.Agent, PrincipalIds.KindOf(p.Id));
    }

    [Fact]
    public async Task An_automation_has_no_credential_and_acts_for_its_owner()
    {
        var f = AutomationMutationRunnerTests.Build();
        f.Whitelist.Allowed.Add("ops@example.com");
        f.Roles.Map["ops@example.com"] = AppRole.Operator;
        var owner = PrincipalIds.User("ops@example.com");

        foreach (var actor in new[]
                 {
                     AutomationActor.ForTask("t1", "nightly", owner, owner),
                     AutomationActor.ForCheck("c1", "web", owner),
                     AutomationActor.ForGitDeploy("g1", "shop", owner),
                     AutomationActor.ForAutoUpdate(owner),
                 })
            await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);

        Assert.Equal(4, f.Audit.Principals.Count);
        Assert.All(f.Audit.Principals, p =>
        {
            Assert.Equal(MutationPrincipalKind.Automation, p.Kind);
            Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf(p.Id));
            Assert.Null(p.CredentialId);
            Assert.Equal(owner, p.OnBehalfOf!.Id);
        });
        // The display name of the task does not reach its id.
        Assert.Equal("task:t1", f.Audit.Principals[0].Id);
        Assert.Equal("task:t1", AutomationActor.ForTask("t1", "renamed", owner, owner).RunnerId);
    }

    [Fact]
    public async Task An_automation_without_an_owner_is_recorded_as_unowned_in_the_scheme()
    {
        var f = AutomationMutationRunnerTests.Build(requireOwner: true);

        await f.Runner.RunAsync(AutomationActor.ForTask("t1", "old", null, null), "container.restart",
            new { containerId = "web-1" }, _ => Task.CompletedTask);

        var p = Assert.Single(f.Audit.Principals);
        Assert.Equal("unowned:task:t1", p.Id);
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf(p.Id));
        Assert.Null(p.OnBehalfOf);
    }
}
