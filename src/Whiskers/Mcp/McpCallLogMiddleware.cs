using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Observability;
using Whiskers.Utils;

namespace Whiskers.Mcp;

/// <summary>
/// Records EXTERNAL, DIRECT MCP tool calls (e.g. Claude Code POSTing a <c>tools/call</c> to
/// <c>/mcp</c> without going through the in-process agent) into the Agent-History log.
/// The in-process agent path is already captured by <see cref="Whiskers.Services.Agent.AgentToolInvoker"/>;
/// this middleware covers everything that bypasses it, so EVERY MCP tool call is observable.
/// It only inspects the JSON-RPC envelope (method + params), never alters the request, and never throws.
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

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            // status < 400 ⇒ the transport accepted the request; tool-internal errors surface in the
            // JSON-RPC body (not captured here), but transport success is a useful coarse signal.
            var success = context.Response.StatusCode < 400;
            foreach (var (tool, argsJson) in calls)
                await RecordAsync(context, tool, argsJson, success, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Reads and rewinds the request body, returning every (toolName, argumentsJson) in it.</summary>
    private async Task<List<(string Tool, string? Args)>> SniffToolCallsAsync(HttpContext context)
    {
        var result = new List<(string, string?)>();
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

    private static void AddIfToolCall(JsonElement msg, List<(string, string?)> sink)
    {
        if (msg.ValueKind != JsonValueKind.Object) return;
        if (!msg.TryGetProperty("method", out var method) || method.GetString() != "tools/call") return;
        if (!msg.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object) return;
        if (!p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return;

        var args = p.TryGetProperty("arguments", out var a) ? a.GetRawText() : null;
        sink.Add((name.GetString()!, args));
    }

    private async Task RecordAsync(HttpContext context, string tool, string? argsJson, bool success, long durationMs)
    {
        try
        {
            var sp = context.RequestServices;
            var store = sp.GetService<IMcpCallLogStore>();
            if (store is null) return;

            var perm = sp.GetService<IMcpPermissionService>();
            var (actor, verdict) = ResolveActorAndVerdict(context, perm, tool);
            var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(tool, McpPermissionLevels.Admin);

            await store.RecordAsync(new McpToolCallEntity
            {
                Timestamp = DateTime.UtcNow,
                Actor = actor,
                ActorType = "mcp-direct",
                ToolName = tool,
                Level = level,
                ParamsJson = Cap(SecretRedactor.Redact(argsJson), 4000),
                Verdict = verdict,
                Success = success,
                DurationMs = (int)durationMs,
                ResultSummary = null,
                Error = success ? null : $"HTTP {context.Response.StatusCode}",
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to record external MCP call for {Tool}", tool);
        }
    }

    /// <summary>Identifies the caller (API-key name or web user) and whether the call was permitted.</summary>
    private static (string Actor, string Verdict) ResolveActorAndVerdict(
        HttpContext context, IMcpPermissionService? perm, string tool)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var key = authHeader["Bearer ".Length..];
            var cfg = perm?.ValidateKey(key);
            var actor = cfg is not null
                ? $"key:{(string.IsNullOrWhiteSpace(cfg.Name) ? cfg.Id : cfg.Name)}"
                : "key:unbekannt";
            var allowed = perm?.IsToolAllowed(key, tool) ?? false;
            return (actor, allowed ? "allow" : "deny");
        }

        // Cookie-authenticated web user POSTing directly to /mcp.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var email = context.User.FindFirst(ClaimTypes.Email)?.Value
                        ?? context.User.Identity.Name
                        ?? "web";
            return (email, "allow");
        }

        return ("anonym", "deny");
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
