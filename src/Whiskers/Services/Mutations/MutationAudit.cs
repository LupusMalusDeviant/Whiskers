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
}

/// <summary>
/// Writes to today's <c>AuditLog</c> table. Unlike <c>AuditLogService.LogAsync</c>, which logs and swallows a failed
/// write, every method here lets the exception escape: a mutation whose intent cannot be recorded does not run.
/// Arguments are redacted before they are stored (INV-51). The hash chain (INV-49) comes with WP5.
/// </summary>
public sealed class AuditLogMutationSink : IMutationAuditSink
{
    private const int MaxDetailChars = 4000;
    private const int MaxReasonChars = 1000;
    private const int MaxArgumentChars = 1500;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public AuditLogMutationSink(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? TimeProvider.System;
    }

    public async Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
        ApprovalGateResult? approval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        var entity = NewEntity(request, capability.Id, success: false,
            Details(operationId, request, phase: "intent", status: null, verification: null, reason: null, durationMs: null,
                approval));

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.AuditLog.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
        string? reason, TimeSpan duration, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var entity = await db.AuditLog.FindAsync([auditId], ct)
                     ?? throw new InvalidOperationException($"Audit intent {auditId} for operation {operationId} is missing.");
        entity.Success = status == MutationStatus.Executed && verification != VerificationStatus.Failed;
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
            status = status.ToString(),
            verification = verification.ToString(),
            reason = Field(redactedReason, budget),
            durationMs = (long)duration.TotalMilliseconds,
            intent = intentValue,
        });
        entity.Details = TryFit(budget => Outcome(budget, intent))
                         ?? Fit(budget => Outcome(budget, Field(entity.Details, budget)));
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
        MutationStatus status, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = NewEntity(request, capability?.Id ?? request.CapabilityId, success: false,
            Details(operationId, request, phase: "outcome", status: status.ToString(), verification: null,
                reason: reason, durationMs: 0));

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.AuditLog.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    private AuditLogEntity NewEntity(MutationRequest request, string action, bool success, string details) => new()
    {
        Timestamp = _time.GetUtcNow().UtcDateTime,
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
    };

    private static string Details(string operationId, MutationRequest request, string phase, string? status,
        string? verification, string? reason, long? durationMs, ApprovalGateResult? approval = null) => Fit(budget => JsonSerializer.Serialize(new
    {
        operationId,
        // INV-22: the recorded policy decision this row is about (null when refused before the policy step).
        decisionId = request.DecisionId,
        // R1-13: the AI trigger behind the call, on every row of its run, nested mutations included.
        trigger = request.TriggerId is { } triggerId ? PrincipalIds.Trigger(triggerId) : null,
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
        arguments = Field(Redact(JsonSerializer.Serialize(WithoutPayloads(request.Arguments))),
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
