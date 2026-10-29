using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Steps 5 and 8 of the gateway pipeline. <see cref="BeginAsync"/> writes the intent before anything runs and must
/// throw when it cannot — the gateway then refuses the mutation (PC-D5, INV-48). <see cref="CompleteAsync"/> turns
/// the same record into the outcome, so one mutation attempt is one audit event (INV-45).
/// </summary>
public interface IMutationAuditSink
{
    /// <param name="approval">The human approval the mutation runs under, when policy asked for one.</param>
    Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
        ApprovalGateResult? approval, CancellationToken ct);

    Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
        string? reason, TimeSpan duration, CancellationToken ct);

    /// <summary>A refusal before execution (unknown capability, level, policy). Also throws on failure.</summary>
    Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability, MutationStatus status,
        string reason, CancellationToken ct);

    /// <summary><see cref="RecordDeniedAsync(string, MutationRequest, Capability?, MutationStatus, string, CancellationToken)"/>
    /// with the approval a pending call waits for (R1-19, INV-45).</summary>
    Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability, MutationStatus status,
        string reason, string? approvalId, CancellationToken ct) =>
        RecordDeniedAsync(operationId, request, capability, status, reason, ct);
}

/// <summary>
/// Writes to today's <c>AuditLog</c> table. Unlike <c>AuditLogService.LogAsync</c>, which logs and swallows a failed
/// write, every method here lets the exception escape: a mutation whose intent cannot be recorded does not run.
/// Arguments are redacted before they are stored (INV-51). The hash chain (INV-49) comes with WP5. The security
/// fields (R1-19, INV-45) are columns of their own; the capped <c>Details</c> only repeats them.
/// </summary>
public sealed class AuditLogMutationSink : IMutationAuditSink
{
    private const int MaxDetailChars = 4000;
    private const int MaxReasonChars = 1000;
    private const int MaxArgumentChars = 1500;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly AuditOutbox? _outbox;
    // The events whose intent this process has written and not yet completed (by EventId): recovery leaves them alone.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _eventOf = new();

    /// <param name="outbox">Where a refusal whose insert failed is kept until it is written (R1-19); the app always
    /// registers it.</param>
    public AuditLogMutationSink(IServiceScopeFactory scopes, TimeProvider? time = null, AuditOutbox? outbox = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? TimeProvider.System;
        _outbox = outbox;
    }

    public async Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
        ApprovalGateResult? approval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        var entity = NewEntity(operationId, request, capability.Id, success: false,
            Details(operationId, request, phase: "intent", status: null, verification: null, reason: null, durationMs: null,
                approval));
        entity.Phase = "intent";
        entity.ApprovalId = approval?.ApprovalId;
        entity.ApprovedBy = approval?.ApprovedBy;

        _inFlight[entity.EventId!] = 0;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var id = await InsertOnceAsync(db, entity, ct);
            _eventOf[id] = entity.EventId!;
            return id;
        }
        catch (Exception)
        {
            _inFlight.TryRemove(entity.EventId!, out _);
            // No intent, no mutation: the gateway refuses. The attempt is still one event — the refusal, kept in the
            // outbox under the same event id (if the intent did land after all, the outbox finds it and keeps that).
            if (_outbox is not null)
            {
                Complete(entity, operationId, nameof(MutationStatus.Denied), nameof(VerificationStatus.NotRun),
                    IntentNotWritten, 0);
                _outbox.TryKeep(entity);
            }
            throw;
        }
    }

    /// <summary>The gateway's refusal when the intent cannot be written.</summary>
    internal const string IntentNotWritten = "The audit record could not be written; the mutation was refused.";

    /// <summary>Whether this process wrote the intent with this event id and has not completed it yet.</summary>
    internal bool InFlight(string? eventId) => eventId is not null && _inFlight.ContainsKey(eventId);

    /// <summary>Inserts the event once: when the insert throws but the row is there (its commit answer was lost), that
    /// row is the event. Returns its id.</summary>
    internal static async Task<long> InsertOnceAsync(MetricsDbContext db, AuditLogEntity row, CancellationToken ct)
    {
        db.AuditLog.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var eventId = row.EventId;
            if (await db.AuditLog.AsNoTracking().Where(a => a.EventId == eventId).Select(a => (long?)a.Id)
                    .SingleOrDefaultAsync(ct) is { } id)
                return id;
            throw;
        }
    }

    public async Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
        string? reason, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var entity = await db.AuditLog.FindAsync([auditId], ct)
                         ?? throw new InvalidOperationException($"Audit intent {auditId} for operation {operationId} is missing.");
            Complete(entity, operationId, status.ToString(), verification.ToString(), reason, (long)duration.TotalMilliseconds);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            // Written or not, the gateway is done with it — only now, so recovery never sees it as abandoned while
            // its outcome is being written. A failed outcome is AuditRecovery's from here (kept, or settled from the
            // journal), also when the row could not even be read.
            if (_eventOf.TryRemove(auditId, out var eventId)) _inFlight.TryRemove(eventId, out _);
        }
    }

    /// <summary>Turns an intent row into its outcome: the typed fields, and the details as one document.
    /// <paramref name="outcome"/> is a <see cref="MutationStatus"/> name, or <c>Unknown</c> (recovery: it was running
    /// when Whiskers stopped).</summary>
    internal static void Complete(AuditLogEntity entity, string operationId, string outcome, string verification,
        string? reason, long? durationMs)
    {
        entity.Success = outcome == nameof(MutationStatus.Executed) && verification != nameof(VerificationStatus.Failed);
        entity.Phase = "outcome";
        entity.Outcome = outcome;
        entity.Verification = verification;
        entity.DurationMs = durationMs;
        entity.OperationId ??= operationId;
        var intent = ParseOrRaw(entity.Details);
        var redactedReason = Redact(reason);
        // INV-22: the decision id is kept at the top of the outcome and the row stays valid JSON: the variable-length
        // fields shrink until the serialized document fits (escaping can make it far longer than its text) — the
        // reason first, then the intent (kept as text), at last both dropped.
        string Outcome(int budget, object? intentValue) => JsonSerializer.Serialize(new
        {
            operationId,
            decisionId = DecisionIdOf(intent),
            // Top level like the decision id: a shortened intent must not lose it.
            trigger = StringOf(intent, "trigger"),
            phase = "outcome",
            status = outcome,
            verification,
            reason = Field(redactedReason, budget),
            durationMs,
            intent = intentValue,
        });
        entity.Details = TryFit(budget => Outcome(budget, intent))
                         ?? Fit(budget => Outcome(budget, Field(entity.Details, budget)));
    }

    public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
        MutationStatus status, string reason, CancellationToken ct) =>
        RecordDeniedAsync(operationId, request, capability, status, reason, approvalId: null, ct);

    public async Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
        MutationStatus status, string reason, string? approvalId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = NewEntity(operationId, request, capability?.Id ?? request.CapabilityId, success: false,
            Details(operationId, request, phase: "outcome", status: status.ToString(), verification: null,
                reason: reason, durationMs: 0,
                approval: approvalId is null ? null : new ApprovalGateResult(false, "", approvalId)));
        entity.Phase = "outcome";
        entity.Outcome = status.ToString();
        entity.Verification = nameof(VerificationStatus.NotRun);
        entity.DurationMs = 0;
        entity.ApprovalId = approvalId;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await InsertOnceAsync(db, entity, ct);
        }
        catch (Exception) when (_outbox is not null)
        {
            // No intent row before a refusal: the event itself is kept (redacted, with its id and time) and inserted
            // later (AuditRecovery). The gateway still logs the failure.
            _outbox.TryKeep(entity);
            throw;
        }
    }

    private AuditLogEntity NewEntity(string operationId, MutationRequest request, string action, bool success,
        string details) => new()
    {
        Timestamp = _time.GetUtcNow().UtcDateTime,
        EventId = Guid.NewGuid().ToString("N"),
        Actor = request.Principal.DisplayName,
        // "web" / "mcp" match the rows AuditLogService writes, so the audit page filters keep working.
        ActorType = request.Surface == MutationSurface.Ui ? "web" : request.Surface.ToString().ToLowerInvariant(),
        Action = action,
        TargetType = request.Target.Kind,
        TargetId = request.Target.Id,
        TargetName = request.Target.Name ?? request.Target.Id,
        ServerId = request.Target.ServerId,
        Details = details,
        Success = success,
        OperationId = operationId,
        ParentOperationId = request.ParentOperationId,
        DecisionId = request.DecisionId,
        TriggerId = request.TriggerId is { } triggerId ? PrincipalIds.Trigger(triggerId) : null,
        Surface = request.Surface.ToString(),
        PrincipalKind = request.Principal.Kind.ToString(),
        PrincipalId = request.Principal.Id,
        CredentialId = request.Principal.CredentialId,
        OnBehalfOfId = request.Principal.OnBehalfOf?.Id,
        OnBehalfOfKind = request.Principal.OnBehalfOf?.Kind.ToString(),
        PrincipalSessionId = request.Principal.SessionId,
        DelegatedBy = request.Principal.DelegatedBy,
        SessionId = request.SessionId,
        Arguments = RedactedArguments(request.Arguments),
    };

    private static string Details(string operationId, MutationRequest request, string phase, string? status,
        string? verification, string? reason, long? durationMs, ApprovalGateResult? approval = null) => Fit(budget => JsonSerializer.Serialize(new
    {
        operationId,
        // INV-22: the recorded policy decision this row is about (null when refused before the policy step).
        decisionId = request.DecisionId,
        // R1-13: the AI trigger behind the call, on every row of its run, nested mutations included.
        trigger = request.TriggerId is { } triggerId ? PrincipalIds.Trigger(triggerId) : null,
        // R1-18: the operation this one runs inside, if it is nested.
        parentOperationId = request.ParentOperationId,
        phase,
        status,
        verification,
        // A rule's reason can quote the argument it matched (e.g. a protected name inside SQL text).
        reason = Field(Redact(WithoutPayloadValues(reason, request.Arguments)), budget),
        durationMs,
        approval = approval is null ? null : new { id = approval.ApprovalId, approvedBy = approval.ApprovedBy },
        principal = new
        {
            kind = request.Principal.Kind.ToString(),
            id = request.Principal.Id,
            credential = request.Principal.CredentialId,
            session = request.Principal.SessionId,
            onBehalfOf = request.Principal.OnBehalfOf?.Id,
            onBehalfOfKind = request.Principal.OnBehalfOf?.Kind.ToString(),
            delegatedBy = request.Principal.DelegatedBy,
        },
        sessionId = request.SessionId,
        arguments = Field(RedactedArguments(request.Arguments),
            budget >= MaxReasonChars ? MaxArgumentChars : budget),
    }));

    // URL user info first: a token alone in front of the host (https://<token>@github.com) is no pattern the
    // redactor knows, and a reason can quote a resolved repository URL.
    private static string? Redact(string? text) =>
        text is null ? null : SecretRedactor.Redact(PolicyDecisions.WithoutUrlCredentials(text)!);

    /// <summary>Argument names whose value is free text that can carry credentials in any syntax (SQL
    /// <c>PASSWORD '…'</c>, a shell command, a file body). The pattern-based redactor cannot recognise all of them, so
    /// the audit row keeps only their length (TA-09, "never log secrets").</summary>
    internal static readonly HashSet<string> PayloadArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "sql", "command", "script", "content", "body", "env", "compose", "yaml", "password", "secret", "token",
        // R1-16: free text the gateway now sees for protected-resource checks — never stored in the audit either.
        "composeContent", "envVars",
        // A deployment's planned mounts: a source can come from an .env value.
        "mounts",
    };

    /// <summary>Removes the values of payload arguments from free text such as a refusal reason.</summary>
    internal static string? WithoutPayloadValues(string? text, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (key, value) in arguments)
        {
            if (!PayloadArguments.Contains(key) || value.ValueKind != JsonValueKind.String) continue;
            var payload = value.GetString();
            if (!string.IsNullOrEmpty(payload) && payload.Length >= 3)
                text = text.Replace(payload, "<withheld>", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>The arguments as one JSON object, redacted value by value (R1-19): a payload keeps only its length,
    /// a secret-named key (at any depth) only <c>***</c>, every other string is redacted on its own — so a pattern can
    /// never run across the document's quotes and commas and swallow the arguments after it.</summary>
    internal static string RedactedArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var result = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in arguments)
            result[key] = PayloadArguments.Contains(key)
                ? $"<withheld: {(value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : value.GetRawText().Length)} chars>"
                : RedactedValue(key, System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText()));
        return result.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonNode? RedactedValue(string? key, System.Text.Json.Nodes.JsonNode? node)
    {
        if (key is not null && SecretKeyName.IsMatch(key) && node is System.Text.Json.Nodes.JsonValue) return "***";
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                    obj[name] = RedactedValue(name, obj[name]?.DeepClone());
                return obj;
            case System.Text.Json.Nodes.JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = RedactedValue(null, array[i]?.DeepClone());
                return array;
            case System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text):
                return Redact(text);
            default:
                return node;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SecretKeyName = new(
        "(?i)password|passwd|pwd|passwort|kennwort|token|secret|api[_-]?key|access[_-]?key|authorization|bearer|credential",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static IReadOnlyDictionary<string, object?> WithoutPayloads(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments.ToDictionary(a => a.Key, a => PayloadArguments.Contains(a.Key)
            ? (object?)$"<withheld: {(a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()!.Length : a.Value.GetRawText().Length)} chars>"
            : a.Value);

    // The intent is nested as JSON so the outcome row stays one readable document; an intent that is not valid JSON
    // (a row written before R1-12 could be cut off) is kept as text instead.
    private static object? ParseOrRaw(string? json)
    {
        if (json is null) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return json; }
    }

    /// <summary>Shortens one text field, so the document around it stays valid JSON. A negative budget drops it.</summary>
    private static string? Field(string? text, int max) =>
        text is null || max < 0 ? null : text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    /// <summary>Builds the details with a shrinking budget for their variable-length fields until the serialized
    /// document fits <see cref="MaxDetailChars"/> — always complete JSON, never a cut-off document.</summary>
    private static string Fit(Func<int, string> build) => TryFit(build) ?? build(-1);

    private static string? TryFit(Func<int, string> build)
    {
        for (var budget = MaxReasonChars; budget > 0; budget /= 2)
        {
            var json = build(budget);
            if (json.Length <= MaxDetailChars) return json;
        }
        return null;
    }

    private static string? DecisionIdOf(object? intent) => StringOf(intent, "decisionId");

    private static string? StringOf(object? intent, string name) =>
        intent is JsonElement { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}
