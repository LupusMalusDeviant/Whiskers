using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Approvals;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Services.Agent.Guardrails;

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
/// pauses, so it becomes approve-then-retry: the first call raises a Human-in-the-Loop approval and is refused;
/// once a human approves it on the Approvals page, the <b>identical</b> call (same principal, tool and arguments)
/// is let through <b>once</b> within <see cref="GrantLifetime"/>. Any change to the arguments needs a new
/// approval — the grant is bound to a hash of exactly what was approved.
/// </summary>
public sealed class DirectMcpCallGuard : IDirectMcpCallGuard
{
    internal static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);
    private const string SessionId = "mcp-direct";
    private const int MaxParamChars = 4000;

    private readonly IAgentGuardrailEngine _engine;
    private readonly IGuardrailStore _policy;
    private readonly IApprovalStore _approvals;
    private readonly INotificationService? _notify;
    private readonly ILogger<DirectMcpCallGuard>? _logger;
    private readonly TimeProvider _time;

    // call fingerprint → approval id still waiting for a human / expiry of an approved, unused grant.
    private readonly ConcurrentDictionary<string, string> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new();

    public DirectMcpCallGuard(
        IAgentGuardrailEngine engine, IGuardrailStore policy, IApprovalStore approvals,
        INotificationService? notify = null, ILogger<DirectMcpCallGuard>? logger = null, TimeProvider? time = null)
    {
        _engine = engine;
        _policy = policy;
        _approvals = approvals;
        _notify = notify;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<DirectCallVerdict> EvaluateAsync(
        AgentPrincipal principal, string toolName, IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(toolName, McpPermissionLevels.Admin);
        var context = new AgentContext(SessionId, principal, AgentOrigin.McpDirect, _policy.Current);
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

        // Confirm → approve-then-retry.
        SweepExpiredGrants();
        var fingerprint = Fingerprint(principal, toolName, arguments);
        if (_grants.TryRemove(fingerprint, out var expires))
        {
            if (expires > _time.GetUtcNow())
                return new DirectCallVerdict(true, GuardrailVerdict.Confirm, null);   // approved, used up now
        }

        if (_pending.TryGetValue(fingerprint, out var pendingId)
            && _approvals.Get(pendingId)?.Status == ApprovalStatus.Pending)
        {
            return new DirectCallVerdict(false, GuardrailVerdict.Confirm,
                $"Approval {pendingId} for '{toolName}' is still pending. Repeat this exact call once a human has approved it.");
        }

        var approval = await RaiseAsync(principal, toolName, level, arguments, decision, fingerprint);
        return new DirectCallVerdict(false, GuardrailVerdict.Confirm,
            $"Human approval required: {decision.Reason} Approval {approval.Id} was raised (Whiskers → Approvals). " +
            $"After it is approved, repeat this exact call within {GrantLifetime.TotalMinutes:0} minutes; it then runs once.");
    }

    private async Task<Approval> RaiseAsync(
        AgentPrincipal principal, string toolName, string level, IReadOnlyDictionary<string, JsonElement> arguments,
        GuardrailDecision decision, string fingerprint)
    {
        var argsJson = JsonSerializer.Serialize(arguments);
        var (server, workload) = ApprovalCoordinator.ExtractTarget(argsJson);
        var actor = ActorOf(principal);
        var request = new ApprovalRequest(
            SessionId: SessionId,
            ToolCallId: Guid.NewGuid().ToString("N"),
            Actor: actor,
            ActorType: "mcp-direct",
            ToolName: toolName,
            Level: level,
            ParamsJson: Cap(SecretRedactor.Redact(argsJson), MaxParamChars),
            Reason: decision.Reason,
            RiskLevel: ApprovalCoordinator.RiskOf(level),
            TargetServer: server,
            TargetWorkload: workload,
            GuardrailPreset: _policy.Config.ActivePreset,
            GuardrailRule: decision.MatchedRuleIds.Count > 0 ? string.Join(", ", decision.MatchedRuleIds) : null);

        var approval = _approvals.Create(request, approved =>
        {
            _pending.TryRemove(fingerprint, out _);
            if (approved)
                _grants[fingerprint] = _time.GetUtcNow().Add(GrantLifetime);
            return Task.CompletedTask;
        });
        _pending[fingerprint] = approval.Id;

        try
        {
            if (_notify is not null)
                await _notify.SendAsync(new NotificationEvent
                {
                    EventType = "agent_approval",
                    ImageInfo = $"MCP client \"{actor}\" wants to run \"{toolName}\" — approval required.",
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Approval notification for direct MCP call {Tool} failed", toolName);
        }
        return approval;
    }

    // Approved calls that were never repeated must not pile up.
    private void SweepExpiredGrants()
    {
        var now = _time.GetUtcNow();
        foreach (var (fingerprint, expires) in _grants)
            if (expires <= now) _grants.TryRemove(fingerprint, out _);
    }

    /// <summary>Who called, which tool, with exactly which arguments (keys sorted, values as raw JSON).</summary>
    internal static string Fingerprint(AgentPrincipal principal, string toolName, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var sb = new StringBuilder();
        sb.Append(ActorOf(principal)).Append('\n').Append(toolName).Append('\n');
        foreach (var (key, value) in arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(value.GetRawText()).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string ActorOf(AgentPrincipal p) =>
        p.McpKeyId is { } id ? $"key:{id}" : p.UserEmail ?? p.DisplayName;

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
