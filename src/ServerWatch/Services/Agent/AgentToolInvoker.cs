using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent.Guardrails;

namespace ServerWatch.Services.Agent;

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
    private readonly ILogger<AgentToolInvoker>? _logger;

    public AgentToolInvoker(
        IAgentToolRegistry registry,
        IAgentGuardrailEngine guardrails,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentToolInvoker>? logger = null)
    {
        _registry = registry;
        _guardrails = guardrails;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
    {
        if (!_registry.Tools.TryGetValue(call.Name, out var entry))
            return Error(call.Id, $"Unbekanntes Tool '{call.Name}'.",
                new GuardrailDecision(GuardrailVerdict.Deny, "Tool nicht im Register.", Array.Empty<string>()));

        var args = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);

        // Authoritative gate. Deny → never execute, no DI scope, no side effect.
        var decision = _guardrails.Evaluate(new GuardrailRequest(entry.Name, entry.RequiredLevel, args, context));
        if (decision.Verdict == GuardrailVerdict.Deny)
        {
            _logger?.LogWarning("Agent-Tool '{Tool}' blockiert: {Reason}", entry.Name, decision.Reason);
            return Error(call.Id, $"Durch Guardrails blockiert: {decision.Reason}", decision);
        }

        try
        {
            var output = await ExecuteAsync(entry, args, context);
            if (output.Length > MaxOutputChars)
                output = output[..MaxOutputChars] + $"\n… [gekürzt, {output.Length - MaxOutputChars} Zeichen mehr]";
            return new AgentToolResult(call.Id, output, false, decision);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent-Tool '{Tool}' fehlgeschlagen", entry.Name);
            return Error(call.Id, $"Fehler bei '{entry.Name}': {ex.Message}", decision);
        }
    }

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

    /// <summary>Synthetic context: AuthDisabled ⇒ the tool-internal CheckAccess calls pass through
    /// (the real gate is the guardrail engine, strictly ≤ principal). The email claim makes the
    /// agent visible in the audit log.</summary>
    private static DefaultHttpContext BuildSyntheticContext(IServiceProvider sp, AgentPrincipal principal)
    {
        var actor = "agent:" + (principal.UserEmail ?? principal.McpKeyId ?? principal.DisplayName);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, actor),
            new Claim(ClaimTypes.Name, principal.DisplayName),
        }, authenticationType: "AuthDisabled");
        return new DefaultHttpContext
        {
            RequestServices = sp,
            User = new ClaimsPrincipal(identity),
        };
    }

    private static AgentToolResult Error(string callId, string message, GuardrailDecision decision)
        => new(callId, message, true, decision);
}
