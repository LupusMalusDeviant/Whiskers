using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Auth;
using Whiskers.Services.Observability;
using Whiskers.Utils;

namespace Whiskers.Services.Agent;

/// <summary>Executes EXACTLY ONE tool call — only after the guardrail gate. The engine is the agent's
/// AUTHORITATIVE gate here: PrincipalCeilingRule guarantees ≤ trigger rights. The real MCP method is
/// invoked via reflection; a synthetic HttpContext (AuthDisabled admin with the agent's email claim)
/// lets the tool-internal checks run and records the agent in the audit log.
/// Without this invoker there is no path to tool execution in the in-process loop.</summary>
public sealed class AgentToolInvoker : IAgentToolInvoker
{
    private const int MaxOutputChars = 8000;

    private readonly IAgentToolRegistry _registry;
    private readonly IAgentGuardrailEngine _guardrails;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMcpCallLogStore? _callLog;
    private readonly ILogger<AgentToolInvoker>? _logger;

    public AgentToolInvoker(
        IAgentToolRegistry registry,
        IAgentGuardrailEngine guardrails,
        IServiceScopeFactory scopeFactory,
        IMcpCallLogStore? callLog = null,
        ILogger<AgentToolInvoker>? logger = null)
    {
        _registry = registry;
        _guardrails = guardrails;
        _scopeFactory = scopeFactory;
        _callLog = callLog;
        _logger = logger;
    }

    public async Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (actor, actorType) = ActorOf(context);

        if (!_registry.Tools.TryGetValue(call.Name, out var entry))
        {
            var unknown = new GuardrailDecision(GuardrailVerdict.Deny, "Tool nicht im Register.", Array.Empty<string>());
            await RecordAsync(call.Name, "?", actor, actorType, call.ArgumentsJson, unknown, sw, false, null, "Unbekanntes Tool");
            return Error(call.Id, $"Unbekanntes Tool '{call.Name}'.", unknown);
        }

        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);

        // Authoritative gate. Deny → never execute, no DI scope, no side effect.
        var decision = _guardrails.Evaluate(new GuardrailRequest(entry.Name, entry.RequiredLevel, args, context));
        if (decision.Verdict == GuardrailVerdict.Deny)
        {
            _logger?.LogWarning("Agent-Tool '{Tool}' blockiert: {Reason}", entry.Name, decision.Reason);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, decision.Reason);
            return Error(call.Id, $"Durch Guardrails blockiert: {decision.Reason}", decision);
        }

        try
        {
            var output = await ExecuteAsync(entry, args, context);
            if (output.Length > MaxOutputChars)
                output = output[..MaxOutputChars] + $"\n… [gekürzt, {output.Length - MaxOutputChars} Zeichen mehr]";
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, true, output, null);
            return new AgentToolResult(call.Id, output, false, decision);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Reflection wraps the tool's real exception — surface the inner message, not the wrapper's.
            var inner = tie.InnerException;
            _logger?.LogError(inner, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, inner.Message);
            return Error(call.Id, $"Fehler bei '{entry.Name}': {inner.Message}", decision);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
            await RecordAsync(entry.Name, entry.RequiredLevel, actor, actorType, call.ArgumentsJson, decision, sw, false, null, ex.Message);
            return Error(call.Id, $"Fehler bei '{entry.Name}': {ex.Message}", decision);
        }
    }

    private static (string actor, string actorType) ActorOf(AgentContext ctx)
    {
        var p = ctx.Principal;
        var actor = p.UserEmail ?? p.McpKeyId ?? p.DisplayName;
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
        GuardrailDecision decision, Stopwatch sw, bool success, string? output, string? error)
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

    private async Task<string> ExecuteAsync(AgentToolEntry entry, IReadOnlyDictionary<string, JsonElement> args, AgentContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = BuildSyntheticContext(sp, context.Principal);
        try
        {
            var arguments = BindArguments(entry.Method, args, sp, out var missing);
            if (missing.Count > 0)
                return $"Fehlende Pflicht-Argumente: {string.Join(", ", missing)}.";

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
        IServiceProvider sp, out List<string> missing)
    {
        missing = new List<string>();
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var isToolArg = p.GetCustomAttribute<DescriptionAttribute>() != null;

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
        var actor = "agent:" + (principal.UserEmail ?? principal.McpKeyId ?? principal.DisplayName);
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
