using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Contract 0010 (INV-25): the database's record that this instance has had a guardrail policy. With the record present
/// and both <c>guardrails.json</c> and <c>guardrails.json.bak</c> missing, the store fails closed instead of creating the
/// SafeDefault. The record is only ever inserted (D2): no member here updates or removes it, and nothing else does.
/// <para>A record write is not a mutation (contract 0009 A1.1): it grants nothing and keeps running under the kill switch.</para>
/// </summary>
public interface IGuardrailPolicyMarker
{
    /// <summary>When the record was written, or <c>null</c> when there is none. Throws on any read error (an unreachable
    /// database, a missing table, a failed query): the caller fails closed (D5).</summary>
    Task<DateTime?> RecordedAtAsync(CancellationToken ct = default);

    /// <summary>Whether the record exists. Throws on any read error.</summary>
    async Task<bool> ExistsAsync(CancellationToken ct = default) => await RecordedAtAsync(ct) is not null;

    /// <summary>D8: whether <c>PolicySnapshots</c> holds any row, i.e. a gateway decision was made under a policy here.
    /// Throws on any read error.</summary>
    Task<bool> PolicySnapshotsExistAsync(CancellationToken ct = default);

    /// <summary>Writes the record if it is absent; an existing record is left as it is. A concurrent duplicate insert
    /// counts as success. <paramref name="source"/> is one of <see cref="GuardrailPolicyMarkerSources"/>.</summary>
    Task EnsureAsync(string source, CancellationToken ct = default);
}

/// <summary>Why the record was written (its <c>Detail</c>, contract 0010 D1).</summary>
public static class GuardrailPolicyMarkerSources
{
    /// <summary>The first-start SafeDefault was saved.</summary>
    public const string Created = "created";
    /// <summary>A valid <c>guardrails.json</c> was loaded at a start.</summary>
    public const string Loaded = "loaded";
    /// <summary>A policy file existed without being loaded (unreadable, or only <c>.bak</c>), or a save wrote it.</summary>
    public const string Backfill = "backfill";
    /// <summary>Both files were missing, but <c>PolicySnapshots</c> proves an earlier policy (D8).</summary>
    public const string SnapshotEvidence = "snapshot-evidence";
    /// <summary>The host CLI <c>--reset-guardrails</c> wrote the SafeDefault (D6).</summary>
    public const string ResetCli = "reset-cli";
}

/// <summary>The record as the row <c>guardrail-policy-existed</c> of <c>InstanceMarkers</c> in the metrics database.</summary>
public sealed class DbGuardrailPolicyMarker : IGuardrailPolicyMarker
{
    /// <summary>The key of the guardrail record.</summary>
    public const string Key = "guardrail-policy-existed";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public DbGuardrailPolicyMarker(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    public async Task<DateTime?> RecordedAtAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.InstanceMarkers.AsNoTracking()
            .Where(m => m.Key == Key)
            .Select(m => (DateTime?)m.RecordedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> PolicySnapshotsExistAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.PolicySnapshots.AsNoTracking().AnyAsync(ct);
    }

    public async Task EnsureAsync(string source, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (await RecordedAtAsync(ct) is not null) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.InstanceMarkers.Add(new InstanceMarkerEntity { Key = Key, RecordedAt = _time.GetUtcNow().UtcDateTime, Detail = source });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another writer inserted it in between: the record exists, which is all that is asked for.
            if (await RecordedAtAsync(ct) is null) throw;
        }
    }
}
