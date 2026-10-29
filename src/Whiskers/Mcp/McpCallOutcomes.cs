using System.Collections.Concurrent;

namespace Whiskers.Mcp;

/// <summary>How one direct MCP tool call ended (R1-20).</summary>
public enum McpCallResult
{
    /// <summary>The tool ran and reported success (for a mutation: the gateway recorded it Executed).</summary>
    Executed,
    /// <summary>Refused before it ran: tool switch, level, scope, guardrail policy.</summary>
    Denied,
    /// <summary>The tool ran and failed — it threw, or answered with an error.</summary>
    Failed,
    /// <summary>Not run yet: the mutation waits for a human approval.</summary>
    PendingApproval,
    /// <summary>The mutation ran, but its verification says it did not take effect.</summary>
    VerificationFailed,
}

/// <summary>
/// The outcome of one tool call, decided by <see cref="McpGuardrailFilter"/> where the call is handled — not derived
/// from the HTTP status, which only says the transport accepted the request.
/// </summary>
/// <param name="RequestId">The call's JSON-RPC id (<see cref="McpCallOutcomes.KeyOf(ModelContextProtocol.Protocol.RequestId?)"/>):
/// each call of a batch keeps its own outcome.</param>
/// <param name="Verdict">allow | confirm | deny.</param>
/// <param name="OperationId">The gateway's operation id for a mutation; null for a read.</param>
public sealed record McpCallOutcome(string? RequestId, string Tool, McpCallResult Result, string Verdict,
    string? OperationId, string? Error)
{
    public bool Success => Result == McpCallResult.Executed;
}

/// <summary>The outcomes of the tool calls in one HTTP request, from the filter to <see cref="McpCallLogMiddleware"/>.</summary>
public static class McpCallOutcomes
{
    private const string ItemKey = "whiskers.mcp.call-outcomes";
    private static readonly object Gate = new();

    /// <summary>Called by the middleware before the request is handled, so the calls of a batch share one list.</summary>
    public static void Begin(HttpContext http) => Of(http);

    /// <summary>A JSON-RPC id as a key that keeps its type: the number <c>1</c> and the string <c>"1"</c> are two calls.</summary>
    public static string? KeyOf(ModelContextProtocol.Protocol.RequestId? id) => id?.Id switch
    {
        string s => "s:" + s,
        null => null,
        var n => "n:" + Convert.ToString(n, System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary><see cref="KeyOf(ModelContextProtocol.Protocol.RequestId?)"/> for the id as it stands in the request body.</summary>
    public static string? KeyOf(System.Text.Json.JsonElement id) => id.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => "s:" + id.GetString(),
        // As the MCP server reads it: an integer (so -0 and 0 are the same id), else its spelling.
        System.Text.Json.JsonValueKind.Number => "n:" + (id.TryGetInt64(out var n)
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : id.GetRawText()),
        _ => null,
    };

    public static void Add(HttpContext http, McpCallOutcome outcome) => Of(http).Enqueue(outcome);

    public static IReadOnlyList<McpCallOutcome> Get(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var value) && value is ConcurrentQueue<McpCallOutcome> queue
            ? queue.ToArray()
            : [];

    private static ConcurrentQueue<McpCallOutcome> Of(HttpContext http)
    {
        // HttpContext.Items is not thread-safe, and the calls of a batch may be handled concurrently.
        lock (Gate)
        {
            if (http.Items.TryGetValue(ItemKey, out var value) && value is ConcurrentQueue<McpCallOutcome> queue)
                return queue;
            var created = new ConcurrentQueue<McpCallOutcome>();
            http.Items[ItemKey] = created;
            return created;
        }
    }
}
