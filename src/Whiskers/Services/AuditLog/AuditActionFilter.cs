using Whiskers.Models;

namespace Whiskers.Services.AuditLog;

/// <summary>
/// The audit page's action filter (review 1.0.0, T3). A filter value is a prefix of the action, which fits the rows the
/// service writes (<c>deploy.app</c>, <c>backup.create</c>, <c>env.update</c>) but not the gateway's, whose action is
/// the capability id (<c>app.deploy</c>, <c>compose.deploy</c>, <c>gitdeploy.deploy</c>, <c>db.backup</c>,
/// <c>selfbackup.restore</c>, <c>container.env_file_save</c>). Three filters therefore found nothing the gateway
/// recorded; they now also match those ids. Every expression stays translatable to SQL.
/// </summary>
public static class AuditActionFilter
{
    public static IQueryable<AuditLogEntity> Apply(IQueryable<AuditLogEntity> query, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return query;
        return filter switch
        {
            "deploy" => query.Where(e => e.Action.StartsWith("deploy") || e.Action.EndsWith(".deploy")
                                         || e.Action.StartsWith("gitdeploy.")),
            "backup" => query.Where(e => e.Action.StartsWith("backup") || e.Action.Contains("backup")
                                         || e.Action.EndsWith(".restore")),
            "env" => query.Where(e => e.Action.StartsWith("env") || e.Action.StartsWith("container.env_")),
            _ => query.Where(e => e.Action.StartsWith(filter)),
        };
    }
}
