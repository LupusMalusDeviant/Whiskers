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
/// fixed texts — a source test (<c>SinkRedactionTests.A_bulk_update_sets_a_redacted_column_only_through_explicit_redaction</c>)
/// holds every <c>ExecuteUpdate</c> that sets a redacted column to a constant or an explicit redaction. Every string column of <see cref="MetricsDbContext"/> is either redacted here or exempt with a reason
/// (contract 0017 C1, <c>SinkRedactionTests.Every_string_column_is_redacted_on_save_or_exempt_with_a_reason</c>): a
/// new column without a decision fails that test.
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
                    // Some writers name a whole command as the target (execute_command): both are redacted.
                    row.TargetId = SinkRedaction.Text(row.TargetId) ?? "";
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
                    // The target is the resolved id or, unresolved, read from the caller's arguments.
                    approval.TargetServer = SinkRedaction.Text(approval.TargetServer);
                    approval.TargetWorkload = SinkRedaction.Text(approval.TargetWorkload);
                    break;
                // What quiet hours held back: the composite redacts the event first, the sink again for any writer.
                // The JSON stays parseable (value by value); the row is found by its id.
                case QuietHoursSuppressionEntity held:
                    held.Target = SinkRedaction.Text(held.Target) ?? "";
                    held.ItemsJson = SinkRedaction.Json(held.ItemsJson) ?? "[]";
                    break;
                case PolicyDecisionEntity decision:
                    decision.Reason = SinkRedaction.Json(decision.Reason);
                    // As in the audit row: a target can be named by a whole command or a repository URL.
                    decision.TargetId = SinkRedaction.Text(decision.TargetId) ?? "";
                    decision.TargetName = SinkRedaction.Text(decision.TargetName);
                    decision.TargetRelated = SinkRedaction.Json(decision.TargetRelated);
                    break;
                case TaskRunHistoryEntity run:
                    run.Output = SinkRedaction.Json(run.Output);
                    run.Error = SinkRedaction.Json(run.Error);
                    break;
                // The notification feed and the alert history (R1-25): the composite redacts the event before it
                // writes them, and so does the sink for any other writer.
                case NotificationEntity notification:
                    notification.Title = SinkRedaction.Text(notification.Title) ?? "";
                    notification.Detail = SinkRedaction.Text(notification.Detail) ?? "";
                    break;
                case AlertHistoryEntity alert:
                    alert.ContainerName = SinkRedaction.Text(alert.ContainerName) ?? "";
                    alert.Message = SinkRedaction.Text(alert.Message) ?? "";
                    break;
                case ScheduledTaskEntity task:
                    // Only the last result: the task's own fields (its command) are what it runs.
                    task.LastResult = SinkRedaction.Json(task.LastResult);
                    break;
                // Contract 0017 C1: the webhook run log and the update history carry command output and exception
                // texts (a git error names its remote with credentials), redacted like a task result.
                case WebhookLogEntity webhookLog:
                    webhookLog.Output = SinkRedaction.Json(webhookLog.Output);
                    webhookLog.Error = SinkRedaction.Json(webhookLog.Error);
                    break;
                case UpdateHistoryEntity update:
                    update.Error = SinkRedaction.Json(update.Error);
                    break;
                // The same exception text the update history gets, kept on the policy as its last result.
                case UpdatePolicyEntity policy:
                    policy.LastUpdateResult = SinkRedaction.Json(policy.LastUpdateResult);
                    break;
                // Free text built from a failed run's exception message or a refusal reason (endpoint checks), and
                // the reason an automatic action was taken (a pause carries the reason it was given).
                case EndpointCheckResultEntity checkResult:
                    checkResult.Detail = SinkRedaction.Text(checkResult.Detail) ?? "";
                    break;
                // Names why a check was suspended; the suspension itself is a bulk update that redacts its own value.
                case EndpointCheckEntity check:
                    check.SuspendedReason = SinkRedaction.Text(check.SuspendedReason);
                    break;
                case ActionOutcomeEntity outcome:
                    outcome.Detail = SinkRedaction.Text(outcome.Detail);
                    outcome.Reason = SinkRedaction.Text(outcome.Reason);
                    break;
                // A note given with a backup is free text from the caller (an MCP key or the UI).
                case VolumeBackupEntity backup:
                    backup.Notes = SinkRedaction.Text(backup.Notes);
                    break;
            }
        }
    }
}
