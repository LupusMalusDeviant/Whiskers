using System.Text.Json;
using ModelContextProtocol.Protocol;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp;

/// <summary>
/// The machine-readable error answer of the tools that promise one (contract 0001, Brokkr provisioning): an MCP tool
/// error whose text is <c>{"error": code, "message": …, "operationId"?: …, "approvalId"?: …}</c> with a code of
/// <see cref="Codes"/>. The tools raise it for what they refuse themselves; <see cref="McpGuardrailFilter"/> raises it
/// for what the gateway refused or held for approval before the tool ran, so a client never parses prose. Every other
/// tool keeps its plain-text errors.
/// </summary>
public static class McpErrorEnvelope
{
    public const string InvalidArgument = "invalid_argument";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Denied = "denied";
    public const string PendingApproval = "pending_approval";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> Codes =
        [InvalidArgument, NotFound, Conflict, Denied, PendingApproval, Failed];

    /// <summary>The tools whose every error is an envelope (contract 0001).</summary>
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "create_git_deploy_app", "set_git_deploy_token", "get_git_deploy_app",
        "create_git_deploy_webhook", "rotate_git_deploy_webhook_secret",
    };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The envelope's text.</summary>
    public static string Json(string code, string message, string? operationId = null, string? approvalId = null) =>
        JsonSerializer.Serialize(new { error = code, message, operationId, approvalId }, Options);

    /// <summary>The envelope as an MCP tool error.</summary>
    public static CallToolResult Result(string code, string message, string? operationId = null, string? approvalId = null) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = Json(code, message, operationId, approvalId) }] };

    /// <summary>The code for a gateway outcome that did not run the tool (or whose effect was not there).</summary>
    public static string CodeOf(MutationStatus status, string? reasonCode) => status switch
    {
        MutationStatus.PendingApproval => PendingApproval,
        MutationStatus.Denied => reasonCode switch
        {
            MutationReasonCodes.TargetUnknown => NotFound,
            MutationReasonCodes.TargetChanged => Conflict,
            MutationReasonCodes.TargetAmbiguous or MutationReasonCodes.TargetNotResolved => InvalidArgument,
            MutationReasonCodes.OperationRunning or MutationReasonCodes.OperationAlreadyRun
                or MutationReasonCodes.OperationIdReused => Conflict,
            _ => Denied,
        },
        _ => Failed,
    };
}
