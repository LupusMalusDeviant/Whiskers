using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D2, INV-01): a person stays keyed by the account handle (<c>user:&lt;email&gt;</c>) and carries, beside
/// it, the credential they signed in with and the id of their web session. A session from before the upgrade has no id
/// and is ended once; an agent acting for the person keeps the person's web session on <c>OnBehalfOf</c>.
/// </summary>
public sealed class HumanPrincipalTests
{
    private static ClaimsPrincipal Session(string scheme, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, scheme));

    [Fact]
    public void Each_sign_in_method_names_its_credential_and_the_session()
    {
        var local = Session(AuthConstants.LocalPasswordScheme, new Claim(ClaimTypes.Email, "ops@example.com"),
            new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim(AuthConstants.SessionIdClaim, "sid-l"));
        var federated = Session(AuthConstants.FederatedScheme, new Claim(ClaimTypes.Email, "ops@example.com"),
            new Claim(AuthConstants.BindingIdClaim, "b-1"), new Claim(AuthConstants.SessionIdClaim, "sid-f"));
        var disabled = Session(AuthConstants.AuthDisabledScheme, new Claim(ClaimTypes.Email, "local@serverwatch.local"));

        Assert.Equal(("local:u-1", "sid-l"), WebSessionClaims.Of(local));
        Assert.Equal(("binding:b-1", "sid-f"), WebSessionClaims.Of(federated));
        Assert.Equal((PrincipalCredentials.AuthDisabled, (string?)null), WebSessionClaims.Of(disabled));
        Assert.Equal(((string?)null, (string?)null), WebSessionClaims.Of(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void A_session_id_is_128_random_bits_and_never_repeats()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => WebSessionClaims.NewSessionId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Equal(22, id.Length));   // 16 bytes, base64url without padding
    }

    [Fact]
    public async Task Two_federated_sign_ins_of_one_person_are_two_sessions_of_one_identity()
    {
        await using var world = await BindingWorld.CreateAsync();
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync("dev@corp.test", AppRole.Operator));

        async Task<ClaimsPrincipal> SignInAsync()
        {
            var context = OidcIdentityBindingTests.Ticket(world.Http(), "Google", new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "g-dev"), new Claim(ClaimTypes.Email, "dev@corp.test"),
                new Claim(AuthConstants.EmailVerifiedClaim, "True"),
            ], "Google"));
            await FederatedSignIn.WhitelistGateAsync(context, "");
            Assert.Null(context.Result);
            return context.Principal!;
        }

        var first = await SignInAsync();
        var second = await SignInAsync();

        var (credential1, session1) = WebSessionClaims.Of(first);
        var (credential2, session2) = WebSessionClaims.Of(second);
        Assert.NotNull(session1);
        Assert.NotNull(session2);
        Assert.NotEqual(session1, session2);
        Assert.StartsWith("binding:", credential1);
        Assert.Equal(credential1, credential2);
        Assert.Equal(first.FindFirstValue(ClaimTypes.Email), second.FindFirstValue(ClaimTypes.Email));
        Assert.True((await world.Validator.CheckAsync(first)).Valid);
    }

    [Fact]
    public async Task A_session_without_its_own_id_is_ended_once()
    {
        await using var world = await BindingWorld.CreateAsync();
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync("ops@example.com", AppRole.Operator));
        var session = await world.SignInAsync(OidcIdentityBindingTests.Google("g-ops", "ops@example.com"));
        Assert.True((await world.Validator.CheckAsync(session)).Valid);

        // The same cookie as issued before the upgrade: every claim but the session id.
        var legacy = new ClaimsPrincipal(new ClaimsIdentity(
            session.Claims.Where(c => c.Type != AuthConstants.SessionIdClaim), AuthConstants.FederatedScheme));
        var check = await world.Validator.CheckAsync(legacy);

        Assert.False(check.Valid);
        Assert.Equal(SessionRevocationReasons.SessionIdMissing, check.Reason);
    }

    [Fact]
    public void The_local_sign_in_handler_issues_the_session_claim()
    {
        // The local sign-in is an endpoint of the pipeline; its claims are built in one place.
        var pipeline = File.ReadAllText(Path.Combine(AutomationActorSchemeTests.SourceRoot(), "src", "Whiskers", "Startup",
            "WhiskersPipelineExtensions.cs"));
        var claims = pipeline[pipeline.IndexOf("new Claim(ClaimTypes.NameIdentifier, user.Id)", StringComparison.Ordinal)..];
        claims = claims[..claims.IndexOf("authenticationType: Whiskers.Services.Auth.AuthConstants.LocalPasswordScheme", StringComparison.Ordinal)];
        Assert.Contains("WebSessionClaims.NewSessionClaim()", claims);
    }

    [Fact]
    public async Task A_ui_mutation_carries_the_handle_the_credential_and_the_session()
    {
        var audit = new AutomationMutationRunnerTests.PrincipalAudit();
        var gateway = new MutationGateway(new CapabilityCatalog(),
            AgentToolTestHelpers.Policy(new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault())), audit, [],
            NullLogger<MutationGateway>.Instance);
        var ui = new UiMutationRunner(gateway, new CapabilityCatalog(), new SignedIn("Ops@Example.com", "local:u-1", "sid-9"));

        await ui.RunAsync("container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);

        var p = Assert.Single(audit.Principals);
        Assert.Equal(MutationPrincipalKind.Human, p.Kind);
        Assert.Equal("user:ops@example.com", p.Id);
        Assert.Equal("local:u-1", p.CredentialId);
        Assert.Equal("sid-9", p.SessionId);
    }

    [Fact]
    public void An_agent_keeps_the_persons_web_session_and_its_own()
    {
        var person = new AgentPrincipal(AgentPrincipalKind.WebUser, "Olivia", McpPermissionLevels.Write, null,
            UserEmail: "ops@example.com", CredentialId: "binding:b-1", WebSessionId: "web-1");

        var agent = MutationPrincipals.Agent(person, "agent-sess");

        Assert.Equal("agent-sess", agent.SessionId);
        Assert.Equal("web-1", agent.OnBehalfOf!.SessionId);
        Assert.Equal("binding:b-1", agent.OnBehalfOf.CredentialId);
        Assert.Equal("user:ops@example.com", agent.OnBehalfOf.Id);
        // The round trip to the guardrails' view keeps both.
        var back = MutationPrincipals.ToAgent(agent.OnBehalfOf);
        Assert.Equal("binding:b-1", back.CredentialId);
        Assert.Equal("web-1", back.WebSessionId);
    }

    private sealed class SignedIn(string email, string credential, string session) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Operator);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(AppRole.Operator >= required);
        public Task<(string? CredentialId, string? SessionId)> GetPrincipalFieldsAsync() =>
            Task.FromResult<(string?, string?)>((credential, session));
    }
}
