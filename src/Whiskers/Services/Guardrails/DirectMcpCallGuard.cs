using Whiskers.Services.Mutations;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;

namespace Whiskers.Services.Guardrails;

/// <summary>Outcome of the guardrail gate for one direct MCP tool call.</summary>
public sealed record DirectCallVerdict(bool Allowed, GuardrailVerdict Verdict, string? Message);

/// <summary>Puts DIRECT MCP tool calls (an external client POSTing <c>tools/call</c> to <c>/mcp</c>) through the
/// same guardrail policy the in-process agent obeys. Without it an external client was bounded by its key's
/// scope only, so a protected resource, a forbidden argument, the read-only kill switch or a required
/// approval applied to the agent but not to a script holding a Write key.</summary>
public interface IDirectMcpCallGuard
{
    Task<DirectCallVerdict> EvaluateAsync(
        AgentPrincipal principal, string toolName, IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken ct = default);
}

/// <summary>
/// <c>Deny</c> blocks the call. <c>Confirm</c> cannot pause an HTTP request for a human the way an agent session
/// pauses, so <see cref="IApprovalGate"/> turns it into approve-then-retry: the first call raises a Human-in-the-Loop
/// approval and is refused; once a human approves it, the <b>identical</b> call (same principal, tool and arguments)
/// runs <b>once</b> within ten minutes.
///
/// Since ADR-0005 step 2.1 this guard decides only calls of tools <b>without</b> a capability (reads); mutating tools
/// are decided by the Mutation Gateway, which uses the same engine, policy and approval gate.
/// </summary>
public sealed class DirectMcpCallGuard : IDirectMcpCallGuard
{
    private readonly IAgentGuardrailEngine _engine;
    private readonly IGuardrailStore _policy;
    private readonly IApprovalGate _approvals;
    private readonly ILogger<DirectMcpCallGuard>? _logger;

    public DirectMcpCallGuard(
        IAgentGuardrailEngine engine, IGuardrailStore policy, IApprovalGate approvals,
        ILogger<DirectMcpCallGuard>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _logger = logger;
    }

    public async Task<DirectCallVerdict> EvaluateAsync(
        AgentPrincipal principal, string toolName, IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(toolName, McpPermissionLevels.Admin);
        var context = new AgentContext(DirectSessionId, principal, AgentOrigin.McpDirect, _policy.Current);
        var decision = _engine.Evaluate(new GuardrailRequest(toolName, level, arguments, context));

        switch (decision.Verdict)
        {
            case GuardrailVerdict.Allow:
                return new DirectCallVerdict(true, GuardrailVerdict.Allow, null);

            case GuardrailVerdict.Deny:
                _logger?.LogWarning("Direct MCP call '{Tool}' by {Actor} blocked by guardrails: {Reason}",
                    toolName, ActorOf(principal), decision.Reason);
                return new DirectCallVerdict(false, GuardrailVerdict.Deny, $"Blocked by guardrails: {decision.Reason}");
        }

        var actor = ActorOf(principal);
        var (preset, policy) = _policy.Active;
        var gate = await _approvals.CheckAsync(
            new ApprovalSubject(toolName, level, actor, actor, DirectSessionId, arguments)
            {
                // R1-14: this caller and credential, these arguments, this policy version.
                Binding = ApprovalBinding.OfDirectCall(principal, toolName, level, arguments,
                    Mutations.PolicySnapshot.Of(preset, policy).VersionHash),
                // R1-15: a direct call is never interactive; its caller does not decide it.
                Requesters = [actor],
            },
            decision.Reason, decision.MatchedRuleIds, ct);
        if (!gate.Approved)
            return new DirectCallVerdict(false, GuardrailVerdict.Confirm, gate.Message);
        // A read has no audit intent to wait for: the approval is used now.
        return await _approvals.TryConsumeAsync(gate.ApprovalId!, Guid.NewGuid().ToString("N"), ct)
            ? new DirectCallVerdict(true, GuardrailVerdict.Confirm, null)
            : new DirectCallVerdict(false, GuardrailVerdict.Confirm,
                $"Approval {gate.ApprovalId} is no longer usable (used by another call or expired); repeat the call to raise a new one.");
    }

    private const string DirectSessionId = "mcp-direct";

    internal static string ActorOf(AgentPrincipal p) => PrincipalIds.Of(p);
}
