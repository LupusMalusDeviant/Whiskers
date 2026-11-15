using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>Contract 0017 C1 (INV-51): every string column of <see cref="MetricsDbContext"/> is decided — redacted by
/// <see cref="SinkRedactionInterceptor"/> or exempt with a reason. A new free-text column without a decision fails.</summary>
public sealed partial class SinkRedactionTests
{
    private const string IdReason = "An id, key or reference Whiskers generates or resolves (GUID, operation/decision id, digest).";
    private const string NameReason = "The name of a server, container, volume, rule or task as Docker or the operator named it.";
    private const string VocabularyReason = "A value from a fixed vocabulary (kind, level, state, verdict, surface, phase).";
    private const string HashReason = "A hash or MAC over other data: no text.";
    private const string ActorReason = "Who acted: an e-mail, an API key's name or a principal id, no credential.";
    private const string ProtectedReason = "Encrypted with the vault before it is written (ProtectedFields); must round-trip verbatim.";
    private const string DefinitionReason = "Configuration the object runs with; rewriting it would change what runs.";

    /// <summary>The string columns the interceptor leaves as they are, each with the reason why.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["ContainerMetricEntity.ContainerId"] = IdReason,
        ["ContainerMetricEntity.ContainerName"] = NameReason,
        ["ContainerMetricEntity.ServerId"] = IdReason,
        ["ServerMetricEntity.ServerId"] = IdReason,
        ["ServerMetricEntity.ServerName"] = NameReason,
        ["ContainerMetricRollupEntity.ServerId"] = IdReason,
        ["ContainerMetricRollupEntity.ContainerId"] = IdReason,
        ["ContainerMetricRollupEntity.ContainerName"] = NameReason,
        ["ServerMetricRollupEntity.ServerId"] = IdReason,
        ["ServerMetricRollupEntity.ServerName"] = NameReason,
        ["MetricRollupStateEntity.Id"] = IdReason,
        ["SelfMetricSampleEntity.Loop"] = VocabularyReason,
        ["SelfMetricSampleEntity.ServerId"] = IdReason,
        ["SelfMetricSampleEntity.SkipReason"] = "A fixed text the loop gives for skipping a round (SelfMetrics.Skip).",

        ["AlertHistoryEntity.ServerId"] = IdReason,
        ["AlertHistoryEntity.ContainerId"] = IdReason,
        ["AlertHistoryEntity.AlertType"] = VocabularyReason,
        ["NotificationEntity.NotificationId"] = IdReason,
        ["NotificationEntity.EventType"] = VocabularyReason,
        ["NotificationEntity.Severity"] = VocabularyReason,
        ["NotificationEntity.Link"] = "A link into Whiskers' own pages, built from ids.",
        ["QuietHoursSuppressionEntity.Id"] = "The lookup key rule|window|target (AlertSuppressionStore.IdOf); it embeds the target, built from the event " +
            "after the composite redacted it (CompositeNotificationService.Redacted). Redacting it here would break the lookup.",
        ["QuietHoursSuppressionEntity.RuleId"] = IdReason,
        ["QuietHoursSuppressionEntity.RuleName"] = NameReason,
        ["QuietHoursSuppressionEntity.WindowKey"] = IdReason,

        ["ActionOutcomeEntity.CorrelationId"] = IdReason,
        ["ActionOutcomeEntity.ActionKind"] = VocabularyReason,
        ["ActionOutcomeEntity.ServerId"] = IdReason,
        ["ActionOutcomeEntity.TargetId"] = IdReason,
        ["ActionOutcomeEntity.TargetName"] = NameReason,

        ["ApprovalRecordEntity.Id"] = IdReason,
        ["ApprovalRecordEntity.SessionId"] = IdReason,
        ["ApprovalRecordEntity.ToolCallId"] = IdReason,
        ["ApprovalRecordEntity.Actor"] = ActorReason,
        ["ApprovalRecordEntity.ActorType"] = VocabularyReason,
        ["ApprovalRecordEntity.ToolName"] = VocabularyReason,
        ["ApprovalRecordEntity.Level"] = VocabularyReason,
        ["ApprovalRecordEntity.CorrelationId"] = IdReason,
        ["ApprovalRecordEntity.RiskLevel"] = VocabularyReason,
        ["ApprovalRecordEntity.GuardrailPreset"] = VocabularyReason,
        ["ApprovalRecordEntity.GuardrailRule"] = "The id of the guardrail rule that asked for the approval.",
        ["ApprovalRecordEntity.ArgumentsHash"] = HashReason,
        ["ApprovalRecordEntity.DecisionId"] = IdReason,
        ["ApprovalRecordEntity.Binding"] = HashReason,
        ["ApprovalRecordEntity.RequesterIds"] = ActorReason,
        ["ApprovalRecordEntity.Status"] = VocabularyReason,
        ["ApprovalRecordEntity.ResolvedBy"] = ActorReason,
        ["ApprovalRecordEntity.ConsumedBy"] = ActorReason,

        ["AuditChainEntry.RowHash"] = HashReason,
        ["AuditChainEntry.PrevHash"] = HashReason,
        ["AuditChainEntry.Hash"] = HashReason,
        ["AuditChainEntry.Mac"] = HashReason,
        ["AuditCheckpointEntity.Kind"] = VocabularyReason,
        ["AuditCheckpointEntity.Hash"] = HashReason,
        ["AuditCheckpointEntity.Mac"] = HashReason,

        ["AuditLogEntity.Actor"] = ActorReason,
        ["AuditLogEntity.ActorType"] = VocabularyReason,
        ["AuditLogEntity.Action"] = VocabularyReason,
        ["AuditLogEntity.TargetType"] = VocabularyReason,
        ["AuditLogEntity.ServerId"] = IdReason,
        ["AuditLogEntity.EventId"] = IdReason,
        ["AuditLogEntity.OperationId"] = IdReason,
        ["AuditLogEntity.ParentOperationId"] = IdReason,
        ["AuditLogEntity.DecisionId"] = IdReason,
        ["AuditLogEntity.TriggerId"] = IdReason,
        ["AuditLogEntity.Surface"] = VocabularyReason,
        ["AuditLogEntity.PrincipalKind"] = VocabularyReason,
        ["AuditLogEntity.PrincipalId"] = ActorReason,
        ["AuditLogEntity.CredentialId"] = "The id of the credential (an API key's id), never the key.",
        ["AuditLogEntity.PrincipalSessionId"] = IdReason,
        ["AuditLogEntity.OnBehalfOfId"] = ActorReason,
        ["AuditLogEntity.OnBehalfOfKind"] = VocabularyReason,
        ["AuditLogEntity.DelegatedBy"] = ActorReason,
        ["AuditLogEntity.SessionId"] = IdReason,
        ["AuditLogEntity.ApprovalId"] = IdReason,
        ["AuditLogEntity.ApprovedBy"] = ActorReason,
        ["AuditLogEntity.Phase"] = VocabularyReason,
        ["AuditLogEntity.Outcome"] = VocabularyReason,
        ["AuditLogEntity.Verification"] = VocabularyReason,

        ["McpToolCallEntity.Actor"] = ActorReason,
        ["McpToolCallEntity.ActorType"] = VocabularyReason,
        ["McpToolCallEntity.ToolName"] = VocabularyReason,
        ["McpToolCallEntity.Level"] = VocabularyReason,
        ["McpToolCallEntity.CorrelationId"] = IdReason,
        ["McpToolCallEntity.Verdict"] = VocabularyReason,
        ["McpToolCallEntity.ServerId"] = IdReason,

        ["OperationRecordEntity.Id"] = IdReason,
        ["OperationRecordEntity.ParentId"] = IdReason,
        ["OperationRecordEntity.CapabilityId"] = VocabularyReason,
        ["OperationRecordEntity.PrincipalId"] = ActorReason,
        ["OperationRecordEntity.InputHash"] = HashReason,
        ["OperationRecordEntity.State"] = VocabularyReason,
        ["OperationRecordEntity.Instance"] = IdReason,
        ["OperationRecordEntity.DecisionId"] = IdReason,
        ["OperationRecordEntity.ApprovalId"] = IdReason,
        ["OperationRecordEntity.Verification"] = VocabularyReason,

        ["PolicyDecisionEntity.Id"] = IdReason,
        ["PolicyDecisionEntity.OperationId"] = IdReason,
        ["PolicyDecisionEntity.PrincipalId"] = ActorReason,
        ["PolicyDecisionEntity.PrincipalKind"] = VocabularyReason,
        ["PolicyDecisionEntity.OnBehalfOfId"] = ActorReason,
        ["PolicyDecisionEntity.CredentialId"] = "The id of the credential (an API key's id), never the key.",
        ["PolicyDecisionEntity.Surface"] = VocabularyReason,
        ["PolicyDecisionEntity.CapabilityId"] = VocabularyReason,
        ["PolicyDecisionEntity.RequiredLevel"] = VocabularyReason,
        ["PolicyDecisionEntity.TargetKind"] = VocabularyReason,
        ["PolicyDecisionEntity.TargetServerId"] = IdReason,
        ["PolicyDecisionEntity.InputHash"] = HashReason,
        ["PolicyDecisionEntity.PolicyVersion"] = HashReason,
        ["PolicyDecisionEntity.Preset"] = VocabularyReason,
        ["PolicyDecisionEntity.MatchedRules"] = "A JSON list of guardrail rule ids.",
        ["PolicyDecisionEntity.Verdict"] = VocabularyReason,
        ["PolicySnapshotEntity.VersionHash"] = HashReason,
        ["PolicySnapshotEntity.Preset"] = VocabularyReason,
        ["PolicySnapshotEntity.PolicyJson"] = DefinitionReason + " The guardrail policy itself; its hash is the version.",

        ["CveFirstSeenEntity.IdentityKey"] = IdReason,
        ["CveFirstSeenEntity.CveId"] = IdReason,
        ["InstanceMarkerEntity.Key"] = IdReason,
        ["InstanceMarkerEntity.Detail"] = "A fixed text naming where the marker came from (IGuardrailPolicyMarker).",

        ["EndpointCheckEntity.Id"] = IdReason,
        ["EndpointCheckEntity.ServerId"] = IdReason,
        ["EndpointCheckEntity.Name"] = NameReason,
        ["EndpointCheckEntity.Target"] = DefinitionReason + " The validator refuses a URL with user info (EndpointCheckValidator).",
        ["EndpointCheckEntity.ExceptionsJson"] = DefinitionReason,
        ["EndpointCheckEntity.OwnerId"] = ActorReason,
        ["EndpointCheckResultEntity.CheckId"] = IdReason,
        ["EndpointCheckResultEntity.OperationId"] = IdReason,
        ["EndpointCheckResultEntity.AnsweredAddress"] = "The IP address that answered the probe.",

        ["LogAlertRuleEntity.RuleId"] = IdReason,
        ["LogAlertRuleEntity.Name"] = NameReason,
        ["LogAlertRuleEntity.ContainerId"] = IdReason,
        ["LogAlertRuleEntity.ContainerName"] = NameReason,
        ["LogAlertRuleEntity.Pattern"] = DefinitionReason + " The pattern the rule matches log lines with.",
        ["LogAlertRuleEntity.Severity"] = VocabularyReason,

        ["ScheduledTaskEntity.TaskId"] = IdReason,
        ["ScheduledTaskEntity.Name"] = NameReason,
        ["ScheduledTaskEntity.CronExpression"] = DefinitionReason,
        ["ScheduledTaskEntity.TargetId"] = IdReason,
        ["ScheduledTaskEntity.TargetName"] = NameReason,
        ["ScheduledTaskEntity.ServerId"] = IdReason,
        ["ScheduledTaskEntity.Config"] = DefinitionReason + " The task's command and settings.",
        ["ScheduledTaskEntity.CreatedBy"] = ActorReason,
        ["ScheduledTaskEntity.CreatedVia"] = VocabularyReason,
        ["TaskRunHistoryEntity.TaskId"] = IdReason,
        ["TaskRunHistoryEntity.TaskName"] = NameReason,
        ["TaskRunHistoryEntity.ProcessInstance"] = IdReason,

        ["UpdatePolicyEntity.ContainerId"] = IdReason,
        ["UpdatePolicyEntity.ContainerName"] = NameReason,
        ["UpdatePolicyEntity.ServerId"] = IdReason,
        ["UpdateHistoryEntity.ContainerId"] = IdReason,
        ["UpdateHistoryEntity.ContainerName"] = NameReason,
        ["UpdateHistoryEntity.ServerId"] = IdReason,
        ["UpdateHistoryEntity.OldImageDigest"] = IdReason,
        ["UpdateHistoryEntity.NewImageDigest"] = IdReason,
        ["UpdateHistoryEntity.Image"] = "An image reference (registry/name:tag); a reference has no place for credentials.",
        ["UpdateHistoryEntity.ProcessInstance"] = IdReason,
        ["UpdateRollbackEntity.ContainerId"] = IdReason,
        ["UpdateRollbackEntity.ContainerName"] = NameReason,
        ["UpdateRollbackEntity.ServerId"] = IdReason,
        ["UpdateRollbackEntity.OldImageRef"] = IdReason,
        ["UpdateRollbackEntity.ConfigJson"] = ProtectedReason + " The container snapshot a rollback recreates from.",

        ["WebhookEntity.WebhookId"] = IdReason,
        ["WebhookEntity.Name"] = NameReason,
        ["WebhookEntity.Secret"] = ProtectedReason + " The webhook's signing secret.",
        ["WebhookEntity.TargetType"] = VocabularyReason,
        ["WebhookEntity.TargetId"] = DefinitionReason + " The container or compose directory the webhook acts on.",
        ["WebhookEntity.Action"] = VocabularyReason,
        ["WebhookEntity.ServerId"] = IdReason,
        ["WebhookEntity.Owner"] = ActorReason,
        ["WebhookEntity.Provider"] = VocabularyReason,
        ["WebhookDeliveryEntity.WebhookId"] = IdReason,
        ["WebhookDeliveryEntity.DeliveryKey"] = IdReason,
        ["WebhookDeliveryEntity.RunId"] = IdReason,
        ["WebhookDeliveryEntity.State"] = VocabularyReason,
        ["WebhookDeliveryEntity.Instance"] = IdReason,
        ["WebhookLogEntity.WebhookId"] = IdReason,
        ["WebhookLogEntity.WebhookName"] = NameReason,
        ["WebhookLogEntity.SourceIp"] = "The caller's IP address.",

        ["VolumeBackupEntity.BackupId"] = IdReason,
        ["VolumeBackupEntity.VolumeName"] = NameReason,
        ["VolumeBackupEntity.ContainerName"] = NameReason,
        ["VolumeBackupEntity.ServerId"] = IdReason,
        ["VolumeBackupEntity.FileName"] = "The archive's file name, built from the volume name and a timestamp.",
    };

    [Fact]
    public void Every_string_column_is_redacted_on_save_or_exempt_with_a_reason()
    {
        using var db = Context();
        var undecided = new List<string>();
        var exemptButRedacted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Text this test cannot set column by column must be decided before it exists: an owned type, a string
        // collection (primitive collection) or a complex property. None exists today.
        var unhandled = db.Model.GetEntityTypes().Where(t => t.IsOwned()).Select(t => $"owned type {t.ClrType.Name}")
            .Concat(db.Model.GetEntityTypes().SelectMany(t => t.GetProperties()
                .Where(p => p.IsPrimitiveCollection || (p.ClrType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.ClrType) && p.ClrType != typeof(byte[])))
                .Select(p => $"collection {t.ClrType.Name}.{p.Name}")))
            .Concat(db.Model.GetEntityTypes().SelectMany(t => t.GetComplexProperties()).Select(p => $"complex {p.DeclaringType.ClrType.Name}.{p.Name}"))
            .ToList();
        Assert.True(unhandled.Count == 0, "INV-51: decide these for the sink and extend this test: " + string.Join(", ", unhandled));

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var columns = entityType.GetProperties()
                .Where(p => p.ClrType == typeof(string) && p.PropertyInfo is { CanWrite: true })
                .ToList();
            if (columns.Count == 0) continue;

            // One row per column: the canary goes into that column only, so another column's redaction cannot hide it.
            foreach (var column in columns)
            {
                var name = $"{entityType.ClrType.Name}.{column.Name}";
                seen.Add(name);
                var row = Activator.CreateInstance(entityType.ClrType)!;
                var secret = $"password={Canary}";
                column.PropertyInfo!.SetValue(row, secret);

                using var single = Context();
                single.Add(row);
                SinkRedactionInterceptor.Redact(single);

                var stored = (string?)column.PropertyInfo.GetValue(row);
                var redacted = stored is null || !stored.Contains(CanaryPrefix, StringComparison.Ordinal);
                var exempt = Exempt.TryGetValue(name, out var reason) && !string.IsNullOrWhiteSpace(reason);
                if (!redacted && !exempt) undecided.Add(name);
                if (redacted && exempt) exemptButRedacted.Add(name);
            }
        }

        Assert.True(undecided.Count == 0,
            "INV-51: string columns neither redacted by SinkRedactionInterceptor nor exempt with a reason: " + string.Join(", ", undecided));
        Assert.True(exemptButRedacted.Count == 0,
            "These columns are redacted, so they do not belong on the exemption list: " + string.Join(", ", exemptButRedacted));
        var stale = Exempt.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Exempt columns that no longer exist: " + string.Join(", ", stale));
    }

    /// <summary>A bulk update (<c>ExecuteUpdate</c>/<c>ExecuteUpdateAsync</c>) passes the interceptor by. Where one sets
    /// a string column the interceptor redacts, the value must be <c>null</c>, a string literal, a <c>const</c> of the
    /// same file, or an explicit <c>SinkRedaction</c>/<c>SecretRedactor</c> call. Matched by column name: a name that
    /// is redacted on any entity counts as redacted (the safe direction).</summary>
    [Fact]
    public void A_bulk_update_sets_a_redacted_column_only_through_explicit_redaction()
    {
        using var db = Context();
        var stringColumns = db.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.ClrType == typeof(string))
                .Select(p => (Name: p.Name, Key: $"{t.ClrType.Name}.{p.Name}")))
            .ToList();
        var redactedNames = stringColumns.Where(c => !Exempt.ContainsKey(c.Key)).Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        var source = Path.Combine(RepoRoot(), "src", "Whiskers");
        var setProperty = new System.Text.RegularExpressions.Regex(@"\.SetProperty\(\s*(\w+)\s*=>\s*\1\.(\w+)\s*,");
        var violations = new List<string>();
        var bulkUpdates = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("ExecuteUpdate", StringComparison.Ordinal)) continue;
            foreach (System.Text.RegularExpressions.Match match in setProperty.Matches(text))
            {
                bulkUpdates++;
                var column = match.Groups[2].Value;
                if (!redactedNames.Contains(column)) continue;
                var value = ArgumentAt(text, match.Index + match.Length).Trim();
                if (IsExplicitlySafe(value, text)) continue;
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(source, file)}:{line} sets {column} to `{value}`");
            }
        }

        Assert.True(bulkUpdates > 0, "the scan found no bulk update at all: the pattern is broken");
        Assert.True(violations.Count == 0,
            "INV-51: a bulk update writes a redacted column past the interceptor without redacting it: " + string.Join("; ", violations));
    }

    private static bool IsExplicitlySafe(string value, string fileText)
    {
        // Only the redacting calls count: SinkRedaction.Cap only shortens, and the other helpers redact nothing.
        if (System.Text.RegularExpressions.Regex.IsMatch(value,
                @"^(?:Whiskers\.Utils\.)?(?:SinkRedaction\.(?:Text|Json|Arguments)|SecretRedactor\.(?:Redact|RedactJson))\("))
            return true;
        var bare = value.Replace("(string?)", "", StringComparison.Ordinal).Trim();
        if (bare == "null" || (bare.StartsWith('"') && bare.EndsWith('"'))) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(bare, @"^\w+$")
            && System.Text.RegularExpressions.Regex.IsMatch(fileText, $@"\bconst\s+string\s+{bare}\b");
    }

    /// <summary>The argument text from <paramref name="start"/> up to the comma or parenthesis that closes it.</summary>
    private static string ArgumentAt(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth == 0: return text[start..i];
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: return text[start..i];
            }
        }
        return text[start..];
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }

    [Fact]
    public async Task The_sink_redacts_webhook_logs_and_update_history_like_task_results()
    {
        const string text = "fatal: unable to access 'https://deploy:s3cretPassw0rd@git.example.com/app.git/': 403";
        var dbPath = Path.Combine(Path.GetTempPath(), $"sw-sink-cols-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={dbPath}").AddInterceptors(SinkRedactionInterceptor.Instance).Options;
            await using (var db = new MetricsDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.TaskRunHistory.Add(new TaskRunHistoryEntity { TaskId = "t", TaskName = "t", StartedAt = DateTime.UtcNow, Output = text });
                db.WebhookLogs.Add(new WebhookLogEntity { WebhookId = "w", WebhookName = "w", Success = false, Output = text, Error = text });
                db.UpdateHistory.Add(new UpdateHistoryEntity { ContainerId = "c", ContainerName = "c", Image = "i", StartedAt = DateTime.UtcNow, Error = text });
                await db.SaveChangesAsync();
            }

            await using var read = new MetricsDbContext(options);
            var task = (await read.TaskRunHistory.AsNoTracking().SingleAsync()).Output;
            var webhookLog = await read.WebhookLogs.AsNoTracking().SingleAsync();
            var update = (await read.UpdateHistory.AsNoTracking().SingleAsync()).Error;
            Assert.DoesNotContain("s3cretPassw0rd", task);   // the control: a task result is redacted
            Assert.False(webhookLog.Error!.Contains("s3cretPassw0rd") || webhookLog.Output!.Contains("s3cretPassw0rd")
                || update!.Contains("s3cretPassw0rd"),
                $"INV-51: the sink left a credential in plaintext. WebhookLogs.Error = \"{webhookLog.Error}\"; " +
                $"WebhookLogs.Output = \"{webhookLog.Output}\"; UpdateHistory.Error = \"{update}\"");
        }
        finally
        {
            TestSqlite.ClearPool(dbPath);
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
