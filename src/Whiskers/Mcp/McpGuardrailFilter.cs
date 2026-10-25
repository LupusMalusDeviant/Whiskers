using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Guardrails;

namespace Whiskers.Mcp;

/// <summary>
/// MCP <c>tools/call</c> filter that runs every DIRECT tool call through <see cref="IDirectMcpCallGuard"/> — the
/// guardrail policy the in-process agent already obeys — before the tool method runs. The tool's own
/// <see cref="McpPermissionCheck"/> (the key's scope) still runs afterwards; this adds the policy on top of it.
/// The in-process agent never passes here (it invokes tools directly and is gated by AgentToolInvoker), so no
/// call is evaluated twice.
///
/// With the agent module disabled the guard is not registered and calls stay bounded by their scope alone —
/// there is then no Approvals page that could ever resolve a <c>Confirm</c>.
/// </summary>
public static class McpGuardrailFilter
{
    /// <summary>HttpContext item carrying the guardrail verdict, so the call log records what was decided.</summary>
    public const string VerdictItemKey = "whiskers.mcp.guardrail-verdict";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (request, ct) =>
    {
        var services = request.Services;
        var guard = services?.GetService<IDirectMcpCallGuard>();
        if (services is null || guard is null || request.Params is null)
            return await next(request, ct);

        // Fail closed: with the guard active, a call whose caller cannot be identified does not run.
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var resolver = services.GetService<IAgentPrincipalResolver>();
        if (http is null || resolver is null)
            return Refuse("Guardrails could not identify the caller; the call was not executed.");

        var principal = resolver.Resolve(http);
        var arguments = request.Params.Arguments is { } args
            ? new Dictionary<string, JsonElement>(args)
            : new Dictionary<string, JsonElement>();

        var verdict = await guard.EvaluateAsync(principal, request.Params.Name, arguments, ct);
        http.Items[VerdictItemKey] = verdict.Allowed ? "allow" : verdict.Verdict.ToString().ToLowerInvariant();
        return verdict.Allowed ? await next(request, ct) : Refuse(verdict.Message ?? "Blocked by guardrails.");
    };

    private static CallToolResult Refuse(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
