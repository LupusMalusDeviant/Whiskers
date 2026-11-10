using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D8, D9, INV-01): an approval is bound to the credential that requested it — a call of the same person
/// under another sign-in cannot use it, and approvals raised before the upgrade (no credential) are unusable — while
/// separation of duties still compares the person (<c>user:&lt;email&gt;</c>). A display string never decides the
/// pending-approval quota.
/// </summary>
public sealed class ApprovalCredentialBindingTests
{
    private const string Alice = "alice@example.com";
    private static readonly Capability Restart = new CapabilityCatalog().Find("container.restart")!;

    private static MutationRequest Call(string? credential, string? session = null) =>
        new("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Alice), Alice, McpPermissionLevels.Admin,
                CredentialId: credential, SessionId: session),
            MutationSurface.Ui, new MutationTarget("container", "web-1") { Resolved = true },
            new Dictionary<string, JsonElement>());

    [Fact]
    public void An_approval_binds_the_requesting_credential_not_the_session()
    {
        var google = ApprovalBinding.Of(Call("binding:b-1", "sid-1"), Restart, "v1");

        Assert.Equal(google, ApprovalBinding.Of(Call("binding:b-1", "sid-1"), Restart, "v1"));
        // Another web session of the same sign-in may use it (a reload, a second tab) …
        Assert.Equal(google, ApprovalBinding.Of(Call("binding:b-1", "sid-2"), Restart, "v1"));
        // … another sign-in method of the same person may not.
        Assert.NotEqual(google, ApprovalBinding.Of(Call("local:u-1", "sid-1"), Restart, "v1"));
        // An approval raised before the upgrade (no credential on record) matches no call made after it.
        Assert.NotEqual(ApprovalBinding.Of(Call(null), Restart, "v1"), google);
    }

    [Fact]
    public async Task An_agent_call_for_the_person_is_bound_to_the_persons_credential_too()
    {
        var person = new AgentPrincipal(AgentPrincipalKind.WebUser, Alice, McpPermissionLevels.Admin, null,
            UserEmail: Alice, CredentialId: "binding:b-1", WebSessionId: "sid-1");
        var other = person with { CredentialId = "local:u-1" };

        var a = ApprovalBinding.Of(Call(null) with { Principal = MutationPrincipals.Agent(person, "s") }, Restart, "v1");
        var b = ApprovalBinding.Of(Call(null) with { Principal = MutationPrincipals.Agent(other, "s") }, Restart, "v1");

        Assert.NotEqual(a, b);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Separation_of_duties_still_compares_the_person_across_sign_in_methods()
    {
        // Alice asked (non-interactive) with her Google sign-in; she cannot decide it with her local sign-in.
        var requesters = ApprovalEligibility.RequestersOf(Call("binding:b-1").Principal);
        Assert.Equal([PrincipalIds.User(Alice)], requesters);
        var store = new ApprovalStore();
        var approval = await store.CreateAsync(new ApprovalRequest("s1", "call-1", Alice, "agent", "restart_container",
            McpPermissionLevels.Write, null, "needs a human", Requesters: requesters), _ => Task.CompletedTask);

        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await store.DecideAsync(approval.Id, true,
            new ApprovalResolver(PrincipalIds.User(Alice), McpPermissionLevels.Admin, Alice)));
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true,
            new ApprovalResolver(PrincipalIds.User("bob@example.com"), McpPermissionLevels.Admin, "bob@example.com")));
    }

    [Fact]
    public async Task A_request_without_requesters_is_refused_not_counted_under_its_display_name()
    {
        var store = new ApprovalStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new ApprovalRequest("s1", "call-1",
            "Olivia", "web", "restart_container", McpPermissionLevels.Write, null, "needs a human"), _ => Task.CompletedTask));
        Assert.Empty(store.GetPending());
    }
}
