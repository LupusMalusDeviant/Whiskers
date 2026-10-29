using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Observability;
using Whiskers.Utils;

namespace Whiskers.Mcp;

/// <summary>
/// Records EXTERNAL, DIRECT MCP tool calls (e.g. Claude Code POSTing a <c>tools/call</c> to
/// <c>/mcp</c> without going through the in-process agent) into the Agent-History log.
/// The in-process agent path is already captured by <see cref="Whiskers.Services.Agent.AgentToolInvoker"/>;
/// this middleware covers everything that bypasses it, so EVERY MCP tool call is observable.
/// It only inspects the JSON-RPC envelope (method + params + id), never alters the request, and never throws.
/// What a call came to is the outcome <see cref="McpGuardrailFilter"/> reported for it (R1-20) — executed, denied,
/// failed, waiting for approval, failed verification — matched by its JSON-RPC id, so each call of a batch keeps its
/// own. The HTTP status is not an outcome: a call the filter reported nothing for is recorded as not successful.
/// </summary>
public sealed class McpCallLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpCallLogMiddleware>? _logger;

    public McpCallLogMiddleware(RequestDelegate next, ILogger<McpCallLogMiddleware>? logger = null)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only POSTs to /mcp carry JSON-RPC tools/call; GET is the SSE stream.
        if (!HttpMethods.IsPost(context.Request.Path.HasValue ? context.Request.Method : "")
            || !context.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(context);
            return;
        }

        // Sniff the body (buffered, then rewound) BEFORE the MCP handler consumes it.
        var calls = await SniffToolCallsAsync(context);
        if (calls.Count == 0)
        {
            await _next(context);
            return;
        }

        McpCallOutcomes.Begin(context);
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var outcomes = McpCallOutcomes.Get(context).ToList();
            foreach (var call in calls)
                await RecordAsync(context, call, Take(outcomes, call), sw.ElapsedMilliseconds);
        }
    }

    /// <summary>The outcome reported for <paramref name="call"/> — the one with its JSON-RPC id, else the first one for
    /// the same tool without an id — removed from <paramref name="outcomes"/> so no outcome counts twice.</summary>
    private static McpCallOutcome? Take(List<McpCallOutcome> outcomes, SniffedCall call)
    {
        var index = call.Id is null
            ? -1
            : outcomes.FindIndex(o => o.RequestId == call.Id && o.Tool == call.Tool);
        if (index < 0)
            index = outcomes.FindIndex(o => o.RequestId is null && o.Tool == call.Tool);
        if (index < 0) return null;
        var outcome = outcomes[index];
        outcomes.RemoveAt(index);
        return outcome;
    }

    private sealed record SniffedCall(string Tool, string? Args, string? Id);

    /// <summary>Reads and rewinds the request body, returning every tool call in it.</summary>
    private async Task<List<SniffedCall>> SniffToolCallsAsync(HttpContext context)
    {
        var result = new List<SniffedCall>();
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(body)) return result;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    AddIfToolCall(el, result);
            }
            else
            {
                AddIfToolCall(root, result);
            }
        }
        catch (Exception ex)
        {
            // Malformed/partial body, non-JSON, etc. — never block the request over logging.
            _logger?.LogDebug(ex, "Could not sniff MCP request body for call logging");
        }
        return result;
    }

    private static void AddIfToolCall(JsonElement msg, List<SniffedCall> sink)
    {
        if (msg.ValueKind != JsonValueKind.Object) return;
        if (!msg.TryGetProperty("method", out var method) || method.GetString() != "tools/call") return;
        if (!msg.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object) return;
        if (!p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return;

        var args = p.TryGetProperty("arguments", out var a) ? a.GetRawText() : null;
        // Keyed as the filter keys the MCP server's id: a number and a string with the same text stay two calls.
        var id = msg.TryGetProperty("id", out var i) ? McpCallOutcomes.KeyOf(i) : null;
        sink.Add(new SniffedCall(name.GetString()!, args, id));
    }

    private async Task RecordAsync(HttpContext context, SniffedCall call, McpCallOutcome? outcome, long durationMs)
    {
        try
        {
            var sp = context.RequestServices;
            var store = sp.GetService<IMcpCallLogStore>();
            if (store is null) return;

            var perm = sp.GetService<IMcpPermissionService>();
            var (actor, verdict) = ResolveActorAndVerdict(context, perm, call.Tool);
            var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(call.Tool, McpPermissionLevels.Admin);

            await store.RecordAsync(new McpToolCallEntity
            {
                Timestamp = DateTime.UtcNow,
                Actor = actor,
                ActorType = "mcp-direct",
                ToolName = call.Tool,
                Level = level,
                ParamsJson = Cap(Redact(call.Args), 4000),
                Verdict = outcome?.Verdict ?? verdict,
                Success = outcome?.Success ?? false,
                DurationMs = (int)durationMs,
                ResultSummary = outcome?.Result.ToString(),
                // The gateway's operation id: the same id as the call's audit event.
                CorrelationId = outcome?.OperationId,
                Error = outcome is null
                    ? $"No outcome was reported for this call (HTTP {context.Response.StatusCode})."
                    : outcome.Success ? null : Cap(Redact(outcome.Error ?? outcome.Result.ToString()), 1000),
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to record external MCP call for {Tool}", call.Tool);
        }
    }

    /// <summary>Identifies the caller (API-key id or web user), and — for a call the filter reported no outcome for —
    /// whether its key may call the tool at all ("deny") or nothing is known ("unknown").</summary>
    private static (string Actor, string Verdict) ResolveActorAndVerdict(
        HttpContext context, IMcpPermissionService? perm, string tool)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var key = authHeader["Bearer ".Length..];
            var cfg = perm?.ValidateKey(key);
            // The key's id, not its name: a rename must not split one caller into two (INV-01).
            var actor = cfg is not null ? PrincipalIds.Key(cfg.Id) : PrincipalIds.Anonymous;
            var allowed = perm?.IsToolAllowed(key, tool) ?? false;
            return (actor, allowed ? "unknown" : "deny");
        }

        // Cookie-authenticated web user POSTing directly to /mcp.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var email = context.User.FindFirst(ClaimTypes.Email)?.Value;
            return (string.IsNullOrWhiteSpace(email) ? PrincipalIds.Anonymous : PrincipalIds.User(email), "unknown");
        }

        return (PrincipalIds.Anonymous, "deny");
    }

    // URL user info first: a token alone in front of the host (https://<token>@host) is no pattern the redactor knows.
    private static string? Redact(string? text) =>
        text is null ? null : SecretRedactor.Redact(PolicyDecisions.WithoutUrlCredentials(text)!);

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
