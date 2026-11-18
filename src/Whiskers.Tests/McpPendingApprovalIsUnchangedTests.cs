using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>Contract 0019 ("Paths that do not change"): a direct MCP call that needs an approval still ends with the
/// pending-approval refusal and an approval another person decides on the page. The key never gets a dialog, and its
/// approval is never one the dialog would show.</summary>
public sealed class McpPendingApprovalIsUnchangedTests
{
    [Fact]
    public async Task A_key_never_gets_an_inline_prompt()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = true;
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: guardrails);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: TestTargets.Resolver(), approvals: new ApprovalGate(approvals, guardrails));
        var prompt = new InlineApprovalRunnerTests.ScriptedPrompt();
        var key = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");
        var capability = new CapabilityCatalog().Find("container.restart")!;
        var args = UiMutationRunner.ToArguments(new { containerId = "web-1" });
        var ran = 0;

        var outcome = await gateway.ExecuteAsync(new MutationRequest(capability.Id, key, MutationSurface.Mcp,
            MutationTargets.FromArguments(capability, args), args), (_, _) => Task.FromResult(++ran));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.Equal(0, ran);
        var approval = approvals.Get(outcome.ApprovalId!)!;
        Assert.Equal("mcp-direct", approval.ActorType);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        // Not an INV-59 hit: the rollout rule never applies to it.
        Assert.False(InlineApproval.Applies(capability.Id, outcome.MatchedRules));

        // Even handed to a UI runner with the dialog wired in, as an INV-59 hit of a listed capability for the very
        // operation, the key's approval is never shown: it was not raised by the web UI for this person.
        var stub = new InlineApprovalRunnerTests.StubGateway(new MutationOutcome<string>(approval.CorrelationId!,
            MutationStatus.PendingApproval, null, "pending", VerificationStatus.NotRun, ApprovalId: approval.Id)
            { MatchedRules = [Whiskers.Services.Guardrails.ProtectedResourceRule.RuleId] });
        var runner = new UiMutationRunner(stub, new CapabilityCatalog(), new InlineApprovalRunnerTests.Person(),
            prompt: prompt, approvals: approvals);
        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync(capability.Id, args,
            (ResolvedTarget _, CancellationToken _) => Task.FromResult("ran")));
        Assert.Equal(approval.Id, refused.ApprovalId);
        Assert.Empty(prompt.Asked);
        Assert.Equal(ApprovalStatus.Pending, approvals.Get(approval.Id)!.Status);
        // The requesting key never decides its own non-interactive request (unchanged, R1-15).
        Assert.NotNull(approvals.CanDecide(approval.Id, new ApprovalResolver("key:k1", McpPermissionLevels.Admin, "ci")));
    }
}
