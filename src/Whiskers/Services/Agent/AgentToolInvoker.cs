using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Auth;
using Whiskers.Services.Observability;
using Whiskers.Utils;

namespace Whiskers.Services.Agent;

/// <summary>Executes EXACTLY ONE tool call — only after the gate. A tool that maps to a capability (a mutation)
/// runs inside <see cref="IMutationGateway"/> (ADR-0005 step 2.1): tool switch and level, target, the guardrail
/// policy, the human approval the session obtained for this very call, audit intent and outcome. Any other tool
/// (a read) passes the guardrail engine here; PrincipalCeilingRule guarantees ≤ trigger rights either way. The real
/// MCP method is invoked via reflection; a synthetic HttpContext carrying the caller's level lets the tool-internal
/// checks run. Without this invoker there is no path to tool execution in the in-process loop.</summary>
public sealed class AgentToolInvoker : IAgentToolInvoker
{
    private const int MaxOutputChars = 8000;

    private readonly IAgentToolRegistry _registry;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMcpCallLogStore? _callLog;
    private readonly ILogger<AgentToolInvoker>? _logger;
    private readonly IMutationGateway? _gateway;
    private readonly ICapabilityCatalog _capabilities;
    private readonly ICapabilityOverrides? _overrides;

    /// <param name="gateway">Required for mutating tools; without it they are refused (fail-closed).</param>
    /// <param name="overrides">Tool switch and stricter stored level for reads (INV-15); mutations get them from the gateway.</param>
    public AgentToolInvoker(
        IAgentToolRegistry registry,
        IAgentGuardrailEngine guardrails,
        IServiceScopeFactory scopeFactory,
        IMcpCallLogStore? callLog = null,
        ILogger<AgentToolInvoker>? logger = null,
        IMutationGateway? gateway = null,
        ICapabilityCatalog? capabilities = null,
        ICapabilityOverrides? overrides = null)
    {
        _registry = registry;
        _guardrails = guardrails;
        _scopeFactory = scopeFactory;
        _callLog = callLog;
        _logger = logger;
        _gateway = gateway;
        _capabilities = capabilities ?? new CapabilityCatalog();
        _overrides = overrides;
    }

    public async Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (actor, actorType) = ActorOf(context);

        if (!_registry.Tools.TryGetValue(call.Name, out var entry))
        {
            var unknown = new GuardrailDecision(GuardrailVerdict.Deny, "Tool not in the registry.", Array.Empty<string>());
            await RecordAsync(call.Name, "?", actor, actorType, call.ArgumentsJson, unknown, sw, false, null, "Unknown tool", call.CorrelationId);
            return Error(call.Id, $"Unknown tool '{call.Name}'.", unknown);
        }

        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);

        if (_capabilities.FindByMcpTool(entry.Name) is { } capability)
            return await InvokeMutationAsync(call, entry, capability, args, context, actor, actorType, sw, ct);

        // Authoritative gate for reads. Deny → never execute, no DI scope, no side effect. The tool switch and a
        // stricter stored level apply to the agent as to every other caller (INV-15).
        var requiredLevel = entry.RequiredLevel;
        if (_overrides is not null)
        {
            var availability = _overrides.ResolveTool(entry.Name, entry.RequiredLevel);
            if (!availability.Enabled)
            {
                var off = new GuardrailDecision(GuardrailVerdict.Deny, $"'{entry.Name}' is disabled in Settings → MCP.", Array.Empty<string>());
                await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, off, sw, false, null, off.Reason, call.CorrelationId);
                return Error(call.Id, off.Reason, off);
            }
            requiredLevel = availability.RequiredLevel;
        }
        var decision = _guardrails.Evaluate(new GuardrailRequest(entry.Name, requiredLevel, args, context));
        if (decision.Verdict == GuardrailVerdict.Deny)
        {
            _logger?.LogWarning("Agent-Tool '{Tool}' blockiert: {Reason}", entry.Name, decision.Reason);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, decision.Reason, call.CorrelationId);
            return Error(call.Id, $"Blocked by guardrails: {decision.Reason}", decision);
        }

        try
        {
            var output = await ExecuteAsync(entry, args, context);
            if (output.Length > MaxOutputChars)
                output = output[..MaxOutputChars] + $"\n… [truncated, {output.Length - MaxOutputChars} more characters]";
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, true, output, null, call.CorrelationId);
            return new AgentToolResult(call.Id, output, false, decision);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Reflection wraps the tool's real exception — surface the inner message, not the wrapper's.
            var inner = tie.InnerException;
            _logger?.LogError(inner, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, inner.Message, call.CorrelationId);
            return Error(call.Id, $"Error in '{entry.Name}': {inner.Message}", decision);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, ex.Message, call.CorrelationId);
            return Error(call.Id, $"Error in '{entry.Name}': {ex.Message}", decision);
        }
    }

    /// <summary>A mutating tool: the gateway decides, executes inside its scope and records the audit event.</summary>
    private async Task<AgentToolResult> InvokeMutationAsync(
        AgentToolCall call, AgentToolEntry entry, Capability capability, IReadOnlyDictionary<string, JsonElement> args,
        AgentContext context, string actor, string actorType, Stopwatch sw, CancellationToken ct)
    {
        if (_gateway is null)
        {
            var refused = new GuardrailDecision(GuardrailVerdict.Deny,
                "The Mutation Gateway is unavailable; the call was not executed.", Array.Empty<string>());
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, refused, sw, false, null, refused.Reason, call.CorrelationId);
            return Error(call.Id, refused.Reason, refused);
        }

        var request = RequestOf(call, capability, args, context);
        // The gateway's token carries the capability deadline and the session's cancellation into the tool.
        // The tool runs on the identity the gateway resolved and decided on (R1-06), not on the raw arguments.
        var outcome = await _gateway.ExecuteAsync(request, (decided, token) =>
            ExecuteAsync(entry, MutationTargets.CanonicalArguments(capability, args, decided.Request.Target,
                takesServerId: entry.Method.GetParameters().Any(p => p.Name == "serverId")), context, token), ct);

        var decision = outcome.Status switch
        {
            MutationStatus.Denied => new GuardrailDecision(GuardrailVerdict.Deny, outcome.Reason ?? "Denied.", outcome.MatchedRules),
            MutationStatus.PendingApproval => new GuardrailDecision(GuardrailVerdict.Confirm, outcome.Reason ?? "Approval required.", outcome.MatchedRules),
            _ => new GuardrailDecision(outcome.ApprovalId is null ? GuardrailVerdict.Allow : GuardrailVerdict.Confirm,
                outcome.ApprovalId is null ? "Allowed." : $"Approved ({outcome.ApprovalId}).", Array.Empty<string>()),
        };

        if (outcome.Status == MutationStatus.Executed)
        {
            var output = outcome.Value ?? "";
            if (output.Length > MaxOutputChars)
                output = output[..MaxOutputChars] + $"\n… [truncated, {output.Length - MaxOutputChars} more characters]";
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, true, output, null, call.CorrelationId);
            return new AgentToolResult(call.Id, output, false, decision);
        }

        if (outcome.Status == MutationStatus.Failed && outcome.Error is null && outcome.Value is { } answer)
        {
            // The tool answered but reported a failure (MutationScope.Fail): the agent sees the tool's own text.
            if (answer.Length > MaxOutputChars)
                answer = answer[..MaxOutputChars] + $"\n… [truncated, {answer.Length - MaxOutputChars} more characters]";
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, answer,
                outcome.Reason, call.CorrelationId);
            return Error(call.Id, answer, decision);
        }

        var error = outcome.Error is TargetInvocationException { InnerException: { } inner } ? inner : outcome.Error;
        if (error is not null)
            _logger?.LogError(error, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
        var message = outcome.Status switch
        {
            MutationStatus.Denied => outcome.Reason ?? "Denied.",
            MutationStatus.PendingApproval => outcome.Reason ?? "Human approval required.",
            _ => $"Error in '{entry.Name}': {error?.Message ?? outcome.Reason}",
        };
        await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null,
            error?.Message ?? outcome.Reason, call.CorrelationId);
        return Error(call.Id, message, decision);
    }

    /// <summary>The agent acts for the user, key or trigger that started it; their rights are its ceiling.</summary>
    public async Task<GuardrailDecision?> DecideAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
    {
        if (_gateway is null || !_registry.Tools.TryGetValue(call.Name, out var entry)
            || _capabilities.FindByMcpTool(entry.Name) is not { } capability)
            return null;
        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);
        var decided = await _gateway.DecideAsync(RequestOf(call, capability, args, context), ct);
        if (decided is null) return null;
        var verdict = decided.Refused is MutationStatus.PendingApproval || decided.Verdict == MutationVerdict.Confirm
            ? GuardrailVerdict.Confirm
            : decided.Refused is not null || decided.Verdict == MutationVerdict.Deny ? GuardrailVerdict.Deny : GuardrailVerdict.Allow;
        return new GuardrailDecision(verdict, decided.Reason, decided.MatchedRules)
        {
            DecisionId = decided.DecisionId,
            Preset = decided.Preset,
            Binding = decided.Binding,
            ResolvedServer = Whiskers.Services.Approvals.ApprovalDisplay.NotifiableTarget(decided.Target).Server,
            ResolvedResource = Whiskers.Services.Approvals.ApprovalDisplay.NotifiableTarget(decided.Target).Resource,
            TargetKind = capability.TargetKind,
        };
    }

    /// <summary>The gateway request for an agent tool call — the same for deciding and for running it.</summary>
    private static MutationRequest RequestOf(AgentToolCall call, Capability capability,
        IReadOnlyDictionary<string, JsonElement> args, AgentContext context) =>
        new(capability.Id, PrincipalOf(context), MutationSurface.Agent, MutationTargets.FromArguments(capability, args), args,
            // The session's approval for this call carries the same correlation id (ApprovalCoordinator).
            OperationId: call.CorrelationId, SessionId: context.SessionId)
        {
            // INV-23: a pinned run (an AI trigger's) is decided against its own preset, not the live active one.
            PinnedPolicy = context.Pinned,
            // R1-15: an approval the gateway raises for this call knows whether a human is present in the chat.
            AgentOrigin = context.Origin,
        };

    internal static MutationPrincipal PrincipalOf(AgentContext ctx) =>
        MutationPrincipals.Agent(ctx.Principal, ctx.SessionId);

    private static (string actor, string actorType) ActorOf(AgentContext ctx)
    {
        var actor = PrincipalIds.Of(ctx.Principal);
        var type = ctx.Origin switch
        {
            AgentOrigin.WebUi => "agent-web",
            AgentOrigin.McpTool => "agent-mcp",
            AgentOrigin.Trigger => "trigger",
            _ => "agent",
        };
        return (actor, type);
    }

    /// <summary>Records the tool call for the Agent-History/observability log (secrets redacted).</summary>
    private async Task RecordAsync(
        string tool, string level, string actor, string actorType, string? rawArgs,
        GuardrailDecision decision, Stopwatch sw, bool success, string? output, string? error,
        string? correlationId)
    {
        if (_callLog is null) return;
        sw.Stop();
        try
        {
            await _callLog.RecordAsync(new McpToolCallEntity
            {
                Timestamp = DateTime.UtcNow,
                Actor = actor,
                ActorType = actorType,
                ToolName = tool,
                Level = level,
                CorrelationId = correlationId,
                ParamsJson = Cap(SecretRedactor.Redact(rawArgs), 4000),
                Verdict = decision.Verdict.ToString().ToLowerInvariant(),
                Success = success,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ResultSummary = output is null ? null : Cap(SecretRedactor.Redact(output), 2000),
                Error = error is null ? null : Cap(error, 1000),
            });
        }
        catch { /* observability must never break tool execution */ }
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);

    private async Task<string> ExecuteAsync(AgentToolEntry entry, IReadOnlyDictionary<string, JsonElement> args,
        AgentContext context, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = BuildSyntheticContext(sp, context.Principal);
        try
        {
            var arguments = BindArguments(entry.Method, args, sp, ct, out var missing);
            if (missing.Count > 0)
                return MutationScope.Fail($"Missing required arguments: {string.Join(", ", missing)}.");

            var target = entry.Method.IsStatic ? null : sp.GetService(entry.Method.DeclaringType!);
            var result = entry.Method.Invoke(target, arguments);
            return result switch
            {
                Task<string> ts => await ts,
                Task t => await AwaitNonGeneric(t),
                string s => s,
                null => "",
                _ => result.ToString() ?? ""
            };
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private static async Task<string> AwaitNonGeneric(Task t)
    {
        await t;
        return "OK";
    }

    private static object?[] BindArguments(
        MethodInfo method, IReadOnlyDictionary<string, JsonElement> args,
        IServiceProvider sp, CancellationToken ct, out List<string> missing)
    {
        missing = new List<string>();
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var isToolArg = p.GetCustomAttribute<DescriptionAttribute>() != null;

            if (p.ParameterType == typeof(CancellationToken))
            {
                values[i] = ct;
                continue;
            }

            if (!isToolArg)
            {
                // DI service from the scope (incl. our synthetic IHttpContextAccessor).
                values[i] = sp.GetService(p.ParameterType);
                continue;
            }

            if (args.TryGetValue(p.Name!, out var el) && el.ValueKind != JsonValueKind.Null)
            {
                values[i] = AgentArgumentBinder.ConvertJson(el, p.ParameterType);
            }
            else if (p.HasDefaultValue)
            {
                values[i] = p.DefaultValue;
            }
            else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null)
            {
                values[i] = null;
            }
            else
            {
                missing.Add(p.Name!);
                values[i] = null;
            }
        }
        return values;
    }

    /// <summary>Synthetic context for tool-internal CheckAccess. Uses a dedicated AgentSynthetic scheme
    /// carrying the caller's real MCP level as a claim — NOT the "AuthDisabled" admin scheme — so
    /// tool-internal RBAC is enforced at the caller's level (defense in depth alongside the guardrail
    /// engine). The email claim makes the agent visible in the audit log.</summary>
    private static DefaultHttpContext BuildSyntheticContext(IServiceProvider sp, AgentPrincipal principal)
    {
        var actor = "agent for " + PrincipalIds.Of(principal);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, actor),
            new Claim(ClaimTypes.Name, principal.DisplayName),
            new Claim(AuthConstants.McpLevelClaim, principal.PermissionLevel),
        }, authenticationType: AuthConstants.AgentSyntheticScheme);
        return new DefaultHttpContext
        {
            RequestServices = sp,
            User = new ClaimsPrincipal(identity),
        };
    }

    private static AgentToolResult Error(string callId, string message, GuardrailDecision decision)
        => new(callId, message, true, decision);
}
