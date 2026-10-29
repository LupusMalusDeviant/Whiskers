using System.Runtime.ExceptionServices;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp;

/// <summary>
/// MCP <c>tools/call</c> filter for every DIRECT tool call (an external client POSTing to <c>/mcp</c>):
/// <list type="bullet">
/// <item>A tool that maps to a capability (a mutation) runs inside <see cref="IMutationGateway"/>: tool switch and
/// level, target resolution, guardrail policy, approval, audit intent, deadline, outcome (ADR-0005 step 2.1).</item>
/// <item>Any other tool (a read) passes the guardrail policy through <see cref="IDirectMcpCallGuard"/>.</item>
/// </list>
/// The tool's own <see cref="McpPermissionCheck"/> (the key's scope) still runs inside. The in-process agent never
/// passes here: it invokes tools directly and reaches the gateway through <c>AgentToolInvoker</c>, so no call is
/// evaluated twice. Guardrails are core (PC-D8); if they are missing anyway, every call is refused (INV-26).
/// Every call's outcome is handed to the call history (<see cref="McpCallOutcomes"/>, R1-20), keyed by its JSON-RPC id.
/// </summary>
public static class McpGuardrailFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => (request, ct) =>
        HandleAsync(request.Services, request.Params, McpCallOutcomes.KeyOf(request.JsonRpcRequest?.Id),
            token => next(request, token), ct);

    /// <summary>The filter without the MCP request around it: <paramref name="requestId"/> is the call's JSON-RPC id.</summary>
    internal static async ValueTask<CallToolResult> HandleAsync(IServiceProvider? services, CallToolRequestParams? call,
        string? requestId, Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken ct)
    {
        if (call is null)
            return await next(ct);
        if (services is null)
            return Refuse("Guardrails are unavailable; the call was not executed.");

        // Fail closed: a call whose caller cannot be identified, or with no policy to apply, does not run.
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var resolver = services.GetService<IAgentPrincipalResolver>();
        var guard = services.GetService<IDirectMcpCallGuard>();
        if (http is null)
            return Refuse("Guardrails could not identify the caller; the call was not executed.");
        var tool = new Call(http, requestId, call.Name);
        if (resolver is null)
            return tool.Deny("Guardrails could not identify the caller; the call was not executed.");
        if (guard is null)
            return tool.Deny("Guardrails are unavailable; the call was not executed.");

        var principal = resolver.Resolve(http);
        var arguments = call.Arguments is { } args
            ? new Dictionary<string, JsonElement>(args)
            : new Dictionary<string, JsonElement>();

        var catalog = services.GetService<ICapabilityCatalog>();
        if (catalog is null)
            return tool.Deny("The capability catalog is unavailable; the call was not executed.");
        var capability = catalog.FindByMcpTool(call.Name);
        if (capability is not null)
        {
            var gateway = services.GetService<IMutationGateway>();
            if (gateway is null)
                return tool.Deny("The Mutation Gateway is unavailable; the call was not executed.");
            return await ThroughGatewayAsync(gateway, capability, principal, arguments, tool, call, next, ct);
        }

        // Reads: the tool switch and level apply to every principal kind, not only to API keys (INV-15). Checking the
        // level here also keeps a key's AllowedTools list from widening what its level permits (INV-16).
        var overrides = services.GetService<ICapabilityOverrides>();
        if (overrides is null)
            return tool.Deny("Tool settings are unavailable; the call was not executed.");
        var availability = overrides.ResolveTool(call.Name,
            McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(call.Name, McpPermissionLevels.Admin));
        if (!availability.Enabled)
            return tool.Deny($"'{call.Name}' is disabled in Settings → MCP.");
        if (!McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(principal.PermissionLevel), availability.RequiredLevel))
            return tool.Deny($"'{call.Name}' requires '{availability.RequiredLevel}'.");

        var verdict = await guard.EvaluateAsync(principal, call.Name, arguments, ct);
        if (!verdict.Allowed)
            return tool.Deny(verdict.Message ?? "Blocked by guardrails.", verdict.Verdict.ToString().ToLowerInvariant());

        CallToolResult result;
        try
        {
            result = await next(ct);
        }
        catch (Exception ex)
        {
            tool.Report(McpCallResult.Failed, "allow", null, ex.Message);
            throw;
        }
        tool.Report(result.IsError == true ? McpCallResult.Failed : McpCallResult.Executed, "allow", null,
            result.IsError == true ? ErrorText(result) : null);
        return result;
    }

    private static async ValueTask<CallToolResult> ThroughGatewayAsync(
        IMutationGateway gateway, Capability capability, AgentPrincipal principal,
        Dictionary<string, JsonElement> arguments, Call tool, CallToolRequestParams call,
        Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken ct)
    {
        var mutation = new MutationRequest(
            capability.Id, PrincipalOf(principal), MutationSurface.Mcp,
            MutationTargets.FromArguments(capability, arguments), arguments);

        MutationOutcome<CallToolResult> outcome;
        try
        {
            outcome = await gateway.ExecuteAsync(mutation, async (decided, token) =>
            {
                // The tool runs on the identity the gateway resolved and decided on (R1-06), not on a second lookup of
                // what the client typed: the target's key and serverId are replaced by their canonical values.
                // A call without an arguments object still receives the server it was decided on.
                var wire = call.Arguments ??= new Dictionary<string, JsonElement>();
                foreach (var (key, value) in MutationTargets.CanonicalArguments(capability, arguments,
                             decided.Request.Target, McpToolParameters.TakesServerId(call.Name)))
                    if (!wire.TryGetValue(key, out var old) || old.GetRawText() != value.GetRawText())
                        wire[key] = value;
                var result = await next(token);
                // A tool result marked as an error is a failed mutation, not a successful one.
                if (result.IsError == true)
                    MutationScope.Fail(ErrorText(result));
                return result;
            }, ct);
        }
        catch (Exception ex)
        {
            // The gateway itself threw (a refusal, also for an intent it could not write, is an outcome, not this): what
            // was decided is unknown, and the call did not succeed.
            tool.Report(McpCallResult.Failed, "unknown", null, ex.Message);
            throw;
        }

        var verdict = outcome.Status switch
        {
            MutationStatus.Denied => "deny",
            MutationStatus.PendingApproval => "confirm",
            _ => outcome.ApprovalId is null ? "allow" : "confirm",
        };
        var result = outcome.Status switch
        {
            MutationStatus.Executed when outcome.Verification == VerificationStatus.Failed => McpCallResult.VerificationFailed,
            MutationStatus.Executed => McpCallResult.Executed,
            MutationStatus.Denied => McpCallResult.Denied,
            MutationStatus.PendingApproval => McpCallResult.PendingApproval,
            _ => McpCallResult.Failed,
        };
        tool.Report(result, verdict, outcome.OperationId, result == McpCallResult.Executed ? null : outcome.Reason);

        return outcome.Status switch
        {
            MutationStatus.Executed => outcome.Value!,
            // The tool answered but reported a failure: the client gets the tool's own answer, marked as an error.
            MutationStatus.Failed when outcome.Error is null && outcome.Value is { } answer => AsError(answer),
            // The tool threw: rethrow so the MCP server reports it exactly as before the gateway existed.
            MutationStatus.Failed when outcome.Error is { } error
                                       && (error is not OperationCanceledException || ct.IsCancellationRequested) =>
                Rethrow(error),
            _ => Refuse(outcome.Reason ?? "The call was not executed."),
        };
    }

    /// <summary>An API key, or a web user calling <c>/mcp</c> with a session cookie.</summary>
    internal static MutationPrincipal PrincipalOf(AgentPrincipal p) => MutationPrincipals.Of(p);

    /// <summary>One call being handled: where its outcome goes.</summary>
    private sealed record Call(HttpContext Http, string? RequestId, string Tool)
    {
        public void Report(McpCallResult result, string verdict, string? operationId, string? error) =>
            McpCallOutcomes.Add(Http, new McpCallOutcome(RequestId, Tool, result, verdict, operationId, error));

        public CallToolResult Deny(string message, string verdict = "deny")
        {
            Report(McpCallResult.Denied, verdict, null, message);
            return Refuse(message);
        }
    }

    private static string ErrorText(CallToolResult result)
    {
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return string.IsNullOrWhiteSpace(text) ? "The tool reported an error." : text;
    }

    private static CallToolResult AsError(CallToolResult result)
    {
        result.IsError = true;
        return result;
    }

    private static CallToolResult Rethrow(Exception error)
    {
        ExceptionDispatchInfo.Capture(error).Throw();
        throw error;   // unreachable
    }

    private static CallToolResult Refuse(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
