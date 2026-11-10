using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Services.AuditLog;

public class AuditLogService : IAuditLogService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogService> _logger;
    private readonly Whiskers.Services.Mutations.IAuditRecovery? _recovery;

    /// <param name="recovery">Told when a row is lost, so the <c>audit</c> health check shows the trail is incomplete
    /// (R1-19).</param>
    public AuditLogService(IServiceScopeFactory scopeFactory, ILogger<AuditLogService> logger,
        Whiskers.Services.Mutations.IAuditRecovery? recovery = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _recovery = recovery;
    }

    public Task LogAsync(string actor, string actorType, string action,
                         string targetType, string targetId, string targetName,
                         string? details = null, string? serverId = null, bool success = true) =>
        TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public Task<bool> TryLogAsync(string actor, string actorType, string action,
                                  string targetType, string targetId, string targetName,
                                  string? details = null, string? serverId = null, bool success = true) =>
        TryLogAtAsync(DateTime.UtcNow, actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action,
                                          string targetType, string targetId, string targetName,
                                          string? details = null, string? serverId = null, bool success = true) =>
        WriteAsync(occurredUtc, actor, actorType, action, targetType, targetId, targetName, details, serverId, success, null);

    /// <inheritdoc />
    public Task<bool> TryLogAsync(Whiskers.Services.Mutations.MutationPrincipal principal, string action,
        string targetType, string targetId, string targetName, string? details = null, string? serverId = null,
        bool success = true, DateTime? occurredUtc = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return WriteAsync(occurredUtc ?? DateTime.UtcNow, principal.DisplayName, AuditPrincipals.ActorTypeOf(principal),
            action, targetType, targetId, targetName, details, serverId, success, principal);
    }

    private async Task<bool> WriteAsync(DateTime occurredUtc, string actor, string actorType, string action,
        string targetType, string targetId, string targetName, string? details, string? serverId, bool success,
        Whiskers.Services.Mutations.MutationPrincipal? principal)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

            // The row and its link of the audit chain are one transaction (R1-23).
            await AuditChain.Of(scope.ServiceProvider).WriteAsync(db, async ct =>
            {
                var row = new AuditLogEntity
                {
                    Timestamp = occurredUtc,
                    Actor = actor,
                    ActorType = actorType,
                    Action = action,
                    TargetType = targetType,
                    TargetId = targetId,
                    TargetName = targetName,
                    Details = details,
                    ServerId = serverId,
                    Success = success,
                    // INV-01 (contract 0014): the structured principal, when the caller named one.
                    PrincipalKind = principal?.Kind.ToString(),
                    PrincipalId = principal?.Id,
                    CredentialId = principal?.CredentialId,
                    PrincipalSessionId = principal?.SessionId,
                    OnBehalfOfId = principal?.OnBehalfOf?.Id,
                    OnBehalfOfKind = principal?.OnBehalfOf?.Kind.ToString(),
                    DelegatedBy = principal?.DelegatedBy,
                };
                db.AuditLog.Add(row);
                await db.SaveChangesAsync(ct);
                return (row.Id, (IReadOnlyCollection<long>)[row.Id]);
            });

            if (IsAccountRow(action))
                // R1-27: a sign-in and identity row names an account by its address — the row keeps it, the application
                // log does not (contract 0003: no address in a log). Nothing of the actor or the target reaches the line;
                // what does is kept on one line.
                _logger.LogInformation("Audit: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} (success={Success})",
                    OneLine(actorType), AccountPlaceholder, OneLine(action), OneLine(targetType), AccountPlaceholder, success);
            else
                _logger.LogInformation("Audit: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} (success={Success})",
                    actorType, actor, action, targetType, targetName, success);
            return true;
        }
        catch (Exception ex) when (IsAccountRow(action))
        {
            // R1-27: what survives here is what happened, without whom; the exception's type only (a message may carry a
            // value). Identity rows carry issuer and subject fingerprint in their details: those stay out of the log; the
            // other sign-in rows' details (reason code and client address) are kept on one line.
            _logger.LogError(
                "AUDIT WRITE FAILED: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} " +
                "(target={TargetId}, server={ServerId}, success={Success}, details={Details}, error={Error})",
                OneLine(actorType), AccountPlaceholder, OneLine(action), OneLine(targetType), AccountPlaceholder,
                AccountPlaceholder, OneLine(serverId), success, LoggedDetails(action, details), ex.GetType().Name);
            _recovery?.LegacyWriteFailed();
            return false;
        }
        catch (Exception ex)
        {
            // Fail-safe: the audit backend is down (DB locked, disk full) but the destructive action already
            // happened — the entry must survive in the app log instead of vanishing. It is redacted exactly as the
            // row would have been (R1-25): the database sink never ran, so nothing else withheld its payloads.
            _logger.LogError(ex,
                "AUDIT WRITE FAILED: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} " +
                "(target={TargetId}, server={ServerId}, success={Success}, details={Details})",
                actorType, actor, action, targetType,
                SinkRedaction.Text(targetName), SinkRedaction.Text(targetId), serverId, success, SinkRedaction.Json(details));
            _recovery?.LegacyWriteFailed();
            return false;
        }
    }

    /// <summary>What the application log shows instead of an account address.</summary>
    public const string AccountPlaceholder = "[account]";

    /// <summary>Rows whose actor and target are a person's account address (sign-ins, sessions, identity bindings —
    /// <c>auth.*</c>): the database keeps the address, the application log never sees it.</summary>
    public static bool IsAccountRow(string? action) => action?.StartsWith("auth.", StringComparison.Ordinal) == true;

    /// <summary>What the application log shows instead of the details of an identity row (issuer, subject fingerprint).</summary>
    public const string WithheldDetails = "[withheld]";

    /// <summary>The details of an <c>auth.*</c> row as a log line may show them: none of an identity row
    /// (<c>auth.identity_*</c>: issuer and subject fingerprint come from the provider's token), the others redacted and
    /// on one line.</summary>
    public static string LoggedDetails(string? action, string? details) =>
        action?.StartsWith("auth.identity_", StringComparison.Ordinal) == true
            ? WithheldDetails
            : OneLine(SinkRedaction.Json(details));

    /// <summary>A value as one log line: CR, LF and the other line breaks become a space, so no value can forge a
    /// line of its own.</summary>
    public static string OneLine(string? value) => (value ?? "")
        .Replace("\r", " ").Replace("\n", " ").Replace("\u0085", " ").Replace("\u2028", " ").Replace("\u2029", " ");

    public async Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
                                                            string? actionFilter = null,
                                                            string? targetTypeFilter = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<AuditLogEntity> query = db.AuditLog;

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action.StartsWith(actionFilter));

        if (!string.IsNullOrEmpty(targetTypeFilter))
            query = query.Where(e => e.TargetType == targetTypeFilter);

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<AuditLogEntity> query = db.AuditLog;

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action.StartsWith(actionFilter));

        if (!string.IsNullOrEmpty(targetTypeFilter))
            query = query.Where(e => e.TargetType == targetTypeFilter);

        return await query.CountAsync();
    }
}
