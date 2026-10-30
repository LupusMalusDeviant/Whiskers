using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Whiskers.Models;
using Whiskers.Utils;

namespace Whiskers.Services.Persistence;

/// <summary>
/// R1-24 (INV-51): the last line before the database. Every free-text column of the audit, history, approval,
/// decision and task-result tables is redacted on save, whoever wrote it — a caller that forgot to redact cannot put
/// a secret on disk. Redaction is idempotent, so text a writer already redacted stays as it is. Bulk updates
/// (<c>ExecuteUpdate</c>) bypass interceptors: their values are built from rows that went through here, or are
/// fixed texts.
/// </summary>
public sealed class SinkRedactionInterceptor : SaveChangesInterceptor
{
    public static readonly SinkRedactionInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) Redact(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Redact(context);
        return ValueTask.FromResult(result);
    }

    /// <summary>Redacts every added or modified sink row tracked by <paramref name="context"/>.</summary>
    public static void Redact(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            switch (entry.Entity)
            {
                case AuditLogEntity row:
                    row.Details = SinkRedaction.Json(row.Details);
                    row.TargetName = SinkRedaction.Text(row.TargetName) ?? "";
                    row.Arguments = SinkRedaction.Arguments(row.Arguments);
                    break;
                case McpToolCallEntity call:
                    call.ParamsJson = SinkRedaction.Arguments(call.ParamsJson);
                    call.ResultSummary = SinkRedaction.Json(call.ResultSummary);
                    call.Error = SinkRedaction.Json(call.Error);
                    break;
                case ApprovalRecordEntity approval:
                    // Not withheld by length: a pending approval read back after a restart must still show the
                    // approver what the call does.
                    approval.ParamsJson = SinkRedaction.Json(approval.ParamsJson);
                    approval.Reason = SinkRedaction.Json(approval.Reason) ?? "";
                    approval.Diff = SinkRedaction.Json(approval.Diff);
                    break;
                case PolicyDecisionEntity decision:
                    decision.Reason = SinkRedaction.Json(decision.Reason);
                    break;
                case TaskRunHistoryEntity run:
                    run.Output = SinkRedaction.Json(run.Output);
                    run.Error = SinkRedaction.Json(run.Error);
                    break;
                case ScheduledTaskEntity task:
                    // Only the last result: the task's own fields (its command) are what it runs.
                    task.LastResult = SinkRedaction.Json(task.LastResult);
                    break;
            }
        }
    }
}
