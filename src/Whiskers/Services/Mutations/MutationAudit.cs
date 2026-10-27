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
        entity.Details = Cap(JsonSerializer.Serialize(new
        {
            operationId,
            phase = "outcome",
            status = status.ToString(),
            verification = verification.ToString(),
            reason = Redact(reason),
            durationMs = (long)duration.TotalMilliseconds,
            intent = ParseOrRaw(entity.Details),
        }));
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
        string? verification, string? reason, long? durationMs, ApprovalGateResult? approval = null) => Cap(JsonSerializer.Serialize(new
    {
        operationId,
        phase,
        status,
        verification,
        reason = Redact(reason),
        durationMs,
        approval = approval is null ? null : new { id = approval.ApprovalId, approvedBy = approval.ApprovedBy },
        principal = new
        {
            kind = request.Principal.Kind.ToString(),
            id = request.Principal.Id,
            credential = request.Principal.CredentialId,
            onBehalfOf = request.Principal.OnBehalfOf?.Id,
        },
        sessionId = request.SessionId,
        arguments = Redact(JsonSerializer.Serialize(WithoutPayloads(request.Arguments))),
    }));

    private static string? Redact(string? text) => text is null ? null : SecretRedactor.Redact(text);

    /// <summary>Argument names whose value is free text that can carry credentials in any syntax (SQL
    /// <c>PASSWORD '…'</c>, a shell command, a file body). The pattern-based redactor cannot recognise all of them, so
    /// the audit row keeps only their length (TA-09, "never log secrets").</summary>
    internal static readonly HashSet<string> PayloadArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "sql", "command", "script", "content", "body", "env", "compose", "yaml", "password", "secret", "token",
    };

    internal static IReadOnlyDictionary<string, object?> WithoutPayloads(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments.ToDictionary(a => a.Key, a => PayloadArguments.Contains(a.Key)
            ? (object?)$"<withheld: {(a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()!.Length : a.Value.GetRawText().Length)} chars>"
            : a.Value);

    // The intent is nested as JSON so the outcome row stays one readable document; a capped (truncated) intent
    // is no longer valid JSON and is kept as text instead.
    private static object? ParseOrRaw(string? json)
    {
        if (json is null) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return json; }
    }

    private static string Cap(string s) => s.Length > MaxDetailChars ? s[..MaxDetailChars] + "…" : s;
}
