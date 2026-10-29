using Whiskers.Services.Mutations;

namespace Whiskers.Services.AuditLog;

/// <summary>
/// The audit a tool writes about its own work (R1-20). A tool that runs inside the Mutation Gateway already has its
/// authoritative event — the gateway's intent and outcome — so what it reports (the record it created, the backup file
/// it wrote, a failure it caught) enriches that event instead of becoming a second security event. A failure the tool
/// reports also makes the mutation Failed: the event cannot claim success for what the tool says went wrong. Outside a
/// gateway scope (a tool that is not a mutation) the report is an ordinary audit row, as before.
/// </summary>
public interface IToolAuditLog
{
    Task LogAsync(string actor, string actorType, string action,
                  string targetType, string targetId, string targetName,
                  string? details = null, string? serverId = null, bool success = true);
}

/// <inheritdoc cref="IToolAuditLog"/>
public sealed class ToolAuditLog : IToolAuditLog
{
    private readonly IAuditLogService _audit;

    public ToolAuditLog(IAuditLogService audit) => _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public Task LogAsync(string actor, string actorType, string action,
                         string targetType, string targetId, string targetName,
                         string? details = null, string? serverId = null, bool success = true)
    {
        // The actor is the gateway's principal there, not what the tool read from the request.
        if (MutationScope.Record(new MutationDomainResult(action, targetType, targetId, targetName, details, serverId, success)))
            return Task.CompletedTask;
        return _audit.LogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);
    }
}
